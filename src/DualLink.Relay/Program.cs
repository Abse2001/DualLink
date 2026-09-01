using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DualLink.Core;
using DualLink.Relay;

if (args.Contains("--tun-smoke", StringComparer.OrdinalIgnoreCase))
{
    using var smokeTun = new LinuxTunDevice("dlbond0");
    await RelayNetwork.ConfigureAsync(CancellationToken.None);
    _ = Task.Run(async () => await smokeTun.ReadAsync(new byte[2048], CancellationToken.None));
    await Task.Delay(100);
    // A minimal IPv4 header is sufficient to prove a write can complete while the
    // independent read descriptor is blocked waiting for an outbound kernel packet.
    byte[] packet = [0x45, 0, 0, 20, 0, 0, 0, 0, 64, 59, 0, 0, 10, 77, 0, 2, 10, 77, 0, 1];
    using var smokeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await smokeTun.WriteAsync(packet, smokeDeadline.Token);
    Console.WriteLine("DualLink TUN full-duplex smoke test passed.");
    Environment.Exit(0);
}

var keyText = Environment.GetEnvironmentVariable("DUALLINK_KEY");
if (string.IsNullOrWhiteSpace(keyText)) throw new InvalidOperationException("DUALLINK_KEY is required.");
var key = Convert.FromBase64String(keyText);
if (key.Length < 32) throw new InvalidOperationException("DUALLINK_KEY must decode to at least 32 bytes.");

var port = int.TryParse(Environment.GetEnvironmentVariable("DUALLINK_PORT"), out var configuredPort)
    ? configuredPort : 443;
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

using var tun = new LinuxTunDevice("dlbond0");
await RelayNetwork.ConfigureAsync(shutdown.Token);
using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
var peers = new ConcurrentDictionary<(ulong Session, byte Path), Peer>();
var replays = new ConcurrentDictionary<ulong, ReplayWindow>();
var reorder = new ConcurrentDictionary<ulong, PacketReorderBuffer>();
long outboundSequence = 0;
var pathPacketCounts = new ConcurrentDictionary<(ulong Session, byte Path), int>();

ulong NextOutboundSequence() => unchecked((ulong)Interlocked.Increment(ref outboundSequence));

Console.WriteLine($"DualLink relay listening on UDP {port}; interface dlbond0 created.");

var receiveTask = Task.Run(async () =>
{
    while (!shutdown.IsCancellationRequested)
    {
        UdpReceiveResult datagram;
        try { datagram = await udp.ReceiveAsync(shutdown.Token); }
        catch (OperationCanceledException) { break; }

        if (!BondingPacketCodec.TryDecode(datagram.Buffer, key, out var packet) || packet is null) continue;
        if (packet.Direction != BondingDirection.Uplink) continue;
        var replay = replays.GetOrAdd(packet.SessionId, _ => new ReplayWindow());
        if (!replay.TryAccept(packet.Sequence)) continue;

        peers[(packet.SessionId, packet.PathId)] = new Peer(datagram.RemoteEndPoint, DateTimeOffset.UtcNow);
        var orderBuffer = reorder.GetOrAdd(packet.SessionId, _ => new PacketReorderBuffer(TimeSpan.FromMilliseconds(150)));
        var ordered = orderBuffer.Add(
            packet.Sequence,
            packet.Kind == BondingPacketKind.Data ? packet.Payload : ReadOnlyMemory<byte>.Empty,
            DateTimeOffset.UtcNow);
        foreach (var innerPacket in ordered)
            if (!innerPacket.IsEmpty) await tun.WriteAsync(innerPacket, shutdown.Token);

        if (packet.Kind == BondingPacketKind.Probe)
        {
            var reply = BondingPacketCodec.Encode(packet with
            {
                Kind = BondingPacketKind.ProbeReply,
                Sequence = NextOutboundSequence(),
                Direction = BondingDirection.Downlink,
                Payload = packet.Payload
            }, key);
            await udp.SendAsync(reply, datagram.RemoteEndPoint, shutdown.Token);
        }
        else if (packet.Kind == BondingPacketKind.Data &&
                 pathPacketCounts.AddOrUpdate((packet.SessionId, packet.PathId), 1, (_, count) => count + 1) % 8 == 0)
        {
            var acknowledgement = BondingPacketCodec.Encode(new BondingPacket(
                BondingPacketKind.Ack,
                packet.PathId,
                packet.SessionId,
                NextOutboundSequence(),
                packet.Sequence,
                ReadOnlyMemory<byte>.Empty,
                BondingDirection.Downlink), key);
            await udp.SendAsync(acknowledgement, datagram.RemoteEndPoint, shutdown.Token);
        }
    }
}, shutdown.Token);

var transmitTask = Task.Run(async () =>
{
    var buffer = new byte[BondingPacketCodec.MaximumPayloadSize];
    while (!shutdown.IsCancellationRequested)
    {
        int length;
        try { length = await tun.ReadAsync(buffer, shutdown.Token); }
        catch (OperationCanceledException) { break; }
        if (length == 0) continue;

        var now = DateTimeOffset.UtcNow;
        var active = peers
            .Where(entry => now - entry.Value.LastSeen < TimeSpan.FromSeconds(15))
            .OrderBy(entry => entry.Key.Path)
            .ToArray();
        if (active.Length == 0) continue;

        // Phase-one downlink policy alternates live paths. The adaptive metrics
        // scheduler will replace this after client feedback is connected.
        var sequence = NextOutboundSequence();
        var selected = active[(int)(sequence % (ulong)active.Length)];
        var frame = BondingPacketCodec.Encode(new BondingPacket(
            BondingPacketKind.Data,
            selected.Key.Path,
            selected.Key.Session,
            sequence,
            0,
            buffer.AsMemory(0, length),
            BondingDirection.Downlink), key);
        await udp.SendAsync(frame, selected.Value.EndPoint, shutdown.Token);
    }
}, shutdown.Token);

await Task.WhenAll(receiveTask, transmitTask);

internal sealed record Peer(IPEndPoint EndPoint, DateTimeOffset LastSeen);
