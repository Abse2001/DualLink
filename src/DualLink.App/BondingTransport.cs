using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DualLink.Core;

namespace DualLink.App;

public sealed record BondingPathConfig(byte PathId, string PathName, IPAddress LocalAddress, int InterfaceIndex);

public sealed class BondingPathTransport : IAsyncDisposable
{
    private readonly Socket _socket;

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

    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken token) =>
        _ = await _socket.SendAsync(datagram, SocketFlags.None, token);

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken token) =>
        _socket.ReceiveAsync(buffer, SocketFlags.None, token);

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
    private readonly Dictionary<string, BondingPathTransport> _paths;
    private readonly AdaptiveBondingScheduler _scheduler = new();
    private readonly ReplayWindow _downlinkReplay = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _receiveTasks = [];
    private long _nextSequence;

    public event Func<ReadOnlyMemory<byte>, ValueTask>? PacketReceived;

    public DualPathBondingClient(IEnumerable<BondingPathConfig> paths, IPEndPoint relay, ReadOnlySpan<byte> key)
    {
        if (key.Length < 32) throw new ArgumentException("The bonding key must contain at least 32 bytes.", nameof(key));
        _key = key.ToArray();
        _sessionId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)));
        _paths = paths.ToDictionary(
            path => path.PathName,
            path => new BondingPathTransport(path, relay),
            StringComparer.OrdinalIgnoreCase);
        if (_paths.Count < 1) throw new ArgumentException("At least one physical path is required.", nameof(paths));
    }

    public void Start()
    {
        if (_receiveTasks.Count != 0) return;
        foreach (var path in _paths.Values)
            _receiveTasks.Add(Task.Run(() => ReceiveLoopAsync(path, _shutdown.Token)));
    }

    public async ValueTask<string?> SendPacketAsync(
        ReadOnlyMemory<byte> innerPacket,
        IReadOnlyCollection<BondingPathSample> samples,
        BondingMode mode,
        CancellationToken token)
    {
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
        return pathName;
    }

    public async ValueTask ProbeAllAsync(CancellationToken token)
    {
        foreach (var path in _paths.Values)
        {
            var frame = BondingPacketCodec.Encode(new BondingPacket(
                BondingPacketKind.Probe,
                path.Config.PathId,
                _sessionId,
                NextSequence(),
                0,
                ReadOnlyMemory<byte>.Empty,
                BondingDirection.Uplink), _key);
            await path.SendAsync(frame, token);
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
            catch (SocketException) when (token.IsCancellationRequested) { break; }

            if (!BondingPacketCodec.TryDecode(buffer.AsSpan(0, length), _key, out var packet) || packet is null) continue;
            if (packet.Direction != BondingDirection.Downlink || packet.SessionId != _sessionId) continue;
            lock (_downlinkReplay)
                if (!_downlinkReplay.TryAccept(packet.Sequence)) continue;

            if (packet.Kind == BondingPacketKind.Data && PacketReceived is { } callback)
                await callback(packet.Payload);
        }
    }

    private ulong NextSequence() => unchecked((ulong)Interlocked.Increment(ref _nextSequence));

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var path in _paths.Values) await path.DisposeAsync();
        try { await Task.WhenAll(_receiveTasks); }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        _shutdown.Dispose();
        CryptographicOperations.ZeroMemory(_key);
    }
}
