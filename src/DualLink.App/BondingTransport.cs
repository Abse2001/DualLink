using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using DualLink.Core;

namespace DualLink.App;

public sealed record BondingPathConfig(byte PathId, string PathName, IPAddress LocalAddress, int InterfaceIndex);
public sealed record BondingPathTraffic(string PathName, long UploadedBytes, long DownloadedBytes);

public sealed class BondingPathTransport : IAsyncDisposable
{
    private readonly Socket _socket;
    private long _uploadedBytes;
    private long _downloadedBytes;

    public BondingPathConfig Config { get; }

    public BondingPathTransport(BondingPathConfig config, IPEndPoint relay)
    {
        Config = config;
        if (config.LocalAddress.AddressFamily != relay.AddressFamily)
            throw new ArgumentException("Local adapter and relay address families must match.", nameof(config));

        _socket = new Socket(relay.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (relay.AddressFamily == AddressFamily.InterNetwork)
            _socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(config.InterfaceIndex));
        else
            _socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31, config.InterfaceIndex);
        _socket.Bind(new IPEndPoint(config.LocalAddress, 0));
        _socket.Connect(relay);
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken token)
    {
        var sent = await _socket.SendAsync(datagram, SocketFlags.None, token);
        Interlocked.Add(ref _uploadedBytes, sent);
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
    {
        var received = await _socket.ReceiveAsync(buffer, SocketFlags.None, token);
        Interlocked.Add(ref _downloadedBytes, received);
        return received;
    }

    public BondingPathTraffic GetTraffic() => new(Config.PathName,
        Interlocked.Read(ref _uploadedBytes), Interlocked.Read(ref _downloadedBytes));

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class DualPathBondingClient : IAsyncDisposable
{
    private readonly byte[] _key;
    private readonly ulong _sessionId;
    private readonly ConcurrentDictionary<string, BondingPathTransport> _paths;
    private readonly IPEndPoint _relay;
    private readonly AdaptiveBondingScheduler _scheduler = new();
    private readonly ReplayWindow _downlinkReplay = new();
    private readonly PacketReorderBuffer _downlinkOrder = new(TimeSpan.FromMilliseconds(150));
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _receiveTasks = [];
    private long _nextSequence;
    private readonly ConcurrentDictionary<ulong, SentPacket> _sentPackets = new();
    private readonly ConcurrentDictionary<byte, PathTelemetry> _telemetry = new();
    private readonly object _taskLock = new();
    private readonly TaskCompletionSource _relayReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Func<ReadOnlyMemory<byte>, ValueTask>? PacketReceived;

    public DualPathBondingClient(IEnumerable<BondingPathConfig> paths, IPEndPoint relay, ReadOnlySpan<byte> key)
    {
        if (key.Length < 32) throw new ArgumentException("The bonding key must contain at least 32 bytes.", nameof(key));
        _key = key.ToArray();
        _sessionId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)));
        _relay = relay;
        _paths = new ConcurrentDictionary<string, BondingPathTransport>(paths.ToDictionary(
            path => path.PathName,
            path => new BondingPathTransport(path, relay),
            StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        if (_paths.Count < 1) throw new ArgumentException("At least one physical path is required.", nameof(paths));
    }

    public void Start()
    {
        if (_receiveTasks.Count != 0) return;
        foreach (var path in _paths.Values) StartReceiver(path);
    }

    public async ValueTask<string?> SendPacketAsync(
        ReadOnlyMemory<byte> innerPacket,
        IReadOnlyCollection<BondingPathSample> samples,
        BondingMode mode,
        CancellationToken token)
    {
        if (mode == BondingMode.Redundant)
        {
            var healthy = samples.Where(sample => sample.Online && _paths.ContainsKey(sample.PathId)).ToArray();
            if (healthy.Length == 0) return null;
            var duplicateSequence = NextSequence();
            foreach (var sample in healthy)
            {
                var duplicatePath = _paths[sample.PathId];
                var duplicate = BondingPacketCodec.Encode(new BondingPacket(
                    BondingPacketKind.Data,
                    duplicatePath.Config.PathId,
                    _sessionId,
                    duplicateSequence,
                    0,
                    innerPacket,
                    BondingDirection.Uplink), _key);
                await duplicatePath.SendAsync(duplicate, token);
                RecordSent(duplicateSequence, duplicatePath, innerPacket.Length);
            }
            return string.Join(" + ", healthy.Select(sample => sample.PathId));
        }

        var pathName = _scheduler.SelectPath(samples, innerPacket.Length, mode);
        if (pathName is null || !_paths.TryGetValue(pathName, out var path)) return null;
        var sequence = NextSequence();
        var frame = BondingPacketCodec.Encode(new BondingPacket(
            BondingPacketKind.Data,
            path.Config.PathId,
            _sessionId,
            sequence,
            0,
            innerPacket,
            BondingDirection.Uplink), _key);
        await path.SendAsync(frame, token);
        RecordSent(sequence, path, innerPacket.Length);
        return pathName;
    }

    public async ValueTask ProbeAllAsync(CancellationToken token)
    {
        foreach (var path in _paths.Values)
        {
            var timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var frame = BondingPacketCodec.Encode(new BondingPacket(
                BondingPacketKind.Probe,
                path.Config.PathId,
                _sessionId,
                NextSequence(),
                0,
                timestamp,
                BondingDirection.Uplink), _key);
            await path.SendAsync(frame, token);
        }
    }

    public async Task WaitForRelayAsync(TimeSpan timeout, CancellationToken token)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutSource.CancelAfter(timeout);
        try { await _relayReady.Task.WaitAsync(timeoutSource.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The relay did not answer through any physical connection. Check the server service, key, and UDP port 443.");
        }
    }

    private async Task ReceiveLoopAsync(BondingPathTransport path, CancellationToken token)
    {
        var buffer = new byte[BondingPacketCodec.HeaderSize + BondingPacketCodec.MaximumPayloadSize + BondingPacketCodec.TagSize];
        while (!token.IsCancellationRequested)
        {
            int length;
            try { length = await path.ReceiveAsync(buffer, token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            if (!BondingPacketCodec.TryDecode(buffer.AsSpan(0, length), _key, out var packet) || packet is null) continue;
            if (packet.Direction != BondingDirection.Downlink || packet.SessionId != _sessionId) continue;
            if (packet.Kind == BondingPacketKind.ProbeReply && packet.Payload.Length == sizeof(long))
            {
                _relayReady.TrySetResult();
                var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(packet.Payload.Span));
                var rtt = Math.Max(0.1, (DateTimeOffset.UtcNow - sentAt).TotalMilliseconds);
                _telemetry.AddOrUpdate(packet.PathId, _ => new PathTelemetry(rtt, 0, 10, 1), (_, old) => old with { RttMs = Ewma(old.RttMs, rtt) });
            }
            else if (packet.Kind == BondingPacketKind.Ack)
            {
                ApplyAcknowledgement(packet.PathId, packet.Acknowledgement);
            }
            IReadOnlyList<ReadOnlyMemory<byte>> ordered;
            lock (_downlinkReplay)
            {
                if (!_downlinkReplay.TryAccept(packet.Sequence)) continue;
                ordered = _downlinkOrder.Add(
                    packet.Sequence,
                    packet.Kind == BondingPacketKind.Data ? packet.Payload : ReadOnlyMemory<byte>.Empty,
                    DateTimeOffset.UtcNow);
            }

            if (PacketReceived is { } callback)
                foreach (var innerPacket in ordered)
                    if (!innerPacket.IsEmpty) await callback(innerPacket);
        }
    }

    private ulong NextSequence() => unchecked((ulong)Interlocked.Increment(ref _nextSequence));

    public IReadOnlyCollection<BondingPathSample> GetAdaptiveSamples(IReadOnlyCollection<BondingPathSample> baseline)
    {
        ExpireLostPackets();
        return baseline.Select(sample =>
        {
            if (!_paths.TryGetValue(sample.PathId, out var path) || !_telemetry.TryGetValue(path.Config.PathId, out var measured)) return sample;
            return sample with
            {
                SmoothedRttMs = measured.RttMs > 0 ? measured.RttMs : sample.SmoothedRttMs,
                LossPercent = Math.Max(sample.LossPercent, measured.LossPercent),
                DeliveryRateMbps = measured.DeliveryRateMbps > 0 ? measured.DeliveryRateMbps : sample.DeliveryRateMbps,
                Reliability = Math.Min(sample.Reliability, measured.Reliability)
            };
        }).ToArray();
    }

    public IReadOnlyCollection<BondingPathTraffic> GetTraffic() => _paths.Values.Select(path => path.GetTraffic()).ToArray();

    public async Task UpdatePathsAsync(IEnumerable<BondingPathConfig> configurations)
    {
        var desired = configurations.ToDictionary(path => path.PathName, StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _paths.ToArray())
        {
            if (desired.TryGetValue(existing.Key, out var config) && config == existing.Value.Config) continue;
            if (_paths.TryRemove(existing.Key, out var removed)) await removed.DisposeAsync();
        }
        foreach (var config in desired.Values)
        {
            if (_paths.ContainsKey(config.PathName)) continue;
            var transport = new BondingPathTransport(config, _relay);
            if (_paths.TryAdd(config.PathName, transport)) StartReceiver(transport); else await transport.DisposeAsync();
        }
    }

    private void StartReceiver(BondingPathTransport path)
    {
        var task = Task.Run(() => ReceiveLoopAsync(path, _shutdown.Token));
        lock (_taskLock) _receiveTasks.Add(task);
    }

    private void RecordSent(ulong sequence, BondingPathTransport path, int bytes) =>
        _sentPackets[sequence] = new(path.Config.PathId, bytes, DateTimeOffset.UtcNow);

    private void ApplyAcknowledgement(byte pathId, ulong acknowledgement)
    {
        var now = DateTimeOffset.UtcNow;
        var acknowledged = _sentPackets.Where(item => item.Value.PathId == pathId && item.Key <= acknowledgement).ToArray();
        if (acknowledged.Length == 0) return;
        foreach (var item in acknowledged) _sentPackets.TryRemove(item.Key, out _);
        var first = acknowledged.Min(item => item.Value.SentAt);
        var duration = Math.Max(0.001, (now - first).TotalSeconds);
        var rate = acknowledged.Sum(item => item.Value.Bytes) * 8d / duration / 1_000_000d;
        var ackRtt = Math.Max(0.1, (now - acknowledged.MaxBy(item => item.Key).Value.SentAt).TotalMilliseconds);
        _telemetry.AddOrUpdate(pathId,
            _ => new PathTelemetry(ackRtt, 0, rate, 1),
            (_, old) => old with { RttMs = Ewma(old.RttMs, ackRtt), DeliveryRateMbps = Ewma(old.DeliveryRateMbps, rate), Reliability = Math.Min(1, old.Reliability + .02) });
    }

    private void ExpireLostPackets()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3);
        foreach (var item in _sentPackets.Where(item => item.Value.SentAt < cutoff).ToArray())
        {
            if (!_sentPackets.TryRemove(item.Key, out var lost)) continue;
            _telemetry.AddOrUpdate(lost.PathId,
                _ => new PathTelemetry(0, 5, 1, .9),
                (_, old) => old with { LossPercent = Math.Min(100, Ewma(old.LossPercent, 5)), Reliability = Math.Max(.05, old.Reliability * .95) });
        }
    }

    private static double Ewma(double previous, double sample) => previous <= 0 ? sample : previous * .8 + sample * .2;

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var path in _paths.Values) await path.DisposeAsync();
        Task[] tasks;
        lock (_taskLock) tasks = _receiveTasks.ToArray();
        try { await Task.WhenAll(tasks); }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        _shutdown.Dispose();
        CryptographicOperations.ZeroMemory(_key);
    }

    private sealed record SentPacket(byte PathId, int Bytes, DateTimeOffset SentAt);
    private sealed record PathTelemetry(double RttMs, double LossPercent, double DeliveryRateMbps, double Reliability);
}
