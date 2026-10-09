using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DualLink.App;
using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public sealed class RecoveryRegressionTests
{
    private static ProbeResult Sample(string id, bool good, DateTimeOffset now, double score = 90) =>
        new(id, now, good, good ? 35 : 0, 0, good ? 0 : 100, good ? score : 0);

    [Fact]
    public void OneFilteredProviderDoesNotMarkWifiOfflineOrInventPacketLoss()
    {
        var result = ConnectivityPolicy.Summarize("wifi", DateTimeOffset.UtcNow, [null, 82]);
        Assert.True(result.Online);
        Assert.Equal(0, result.PacketLossPercent);
        Assert.True(result.Score > 75);
        Assert.False(ConnectivityPolicy.Summarize("wifi", DateTimeOffset.UtcNow, [null, null]).Online);
        Assert.Empty(ConnectivityPolicy.PhysicalTargets.Intersect(ConnectivityPolicy.TunnelTargets));
    }

    [Fact]
    public async Task GoodProviderDoesNotWaitForBlockedProvider()
    {
        var blockedCanceled = false;
        var result = await ConnectivityPolicy.FirstSuccessAsync([
            async token => { try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { blockedCanceled = true; } return null; },
            _ => Task.FromResult<double?>(65)
        ], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(65, result);
        Assert.True(blockedCanceled);
    }

    [Fact]
    public void ProbeDeadlineAdaptsToSlowButWorkingWifi()
    {
        Assert.Equal(250, ConnectivityPolicy.ProbeTimeout(125, 35));
        Assert.Equal(900, ConnectivityPolicy.ProbeTimeout(125, 200));
        Assert.Equal(1500, ConnectivityPolicy.ProbeTimeout(125, 600));
    }

    [Fact]
    public void RepeatedUpstreamLossAndRecoveryKeepsPreferenceWithoutFlapping()
    {
        var tracker = new PathHealthTracker();
        var controller = new FailoverController(new());
        var now = DateTimeOffset.UtcNow;
        controller.Evaluate([Sample("ethernet", true, now), Sample("wifi", true, now, 70)], "ethernet");
        tracker.Update(Sample("ethernet", true, now), 1, 2, true);
        for (var cycle = 0; cycle < 100; cycle++)
        {
            now += TimeSpan.FromSeconds(2);
            var dead = tracker.Update(Sample("ethernet", false, now), 1, 2, true, TimeSpan.FromSeconds(1));
            Assert.Equal("wifi", controller.Evaluate([dead, Sample("wifi", true, now, 70)], "ethernet").ActiveAdapterId);
            for (var ms = 0; ms < 1000; ms += 100)
            {
                var recovering = tracker.Update(Sample("ethernet", true, now.AddMilliseconds(ms)), 1, 2, true, TimeSpan.FromSeconds(1));
                Assert.Equal("wifi", controller.Evaluate([recovering, Sample("wifi", true, now, 70)], "ethernet").ActiveAdapterId);
            }
            var restored = tracker.Update(Sample("ethernet", true, now.AddSeconds(1)), 1, 2, true, TimeSpan.FromSeconds(1));
            Assert.Equal("ethernet", controller.Evaluate([restored, Sample("wifi", true, now, 70)], "ethernet").ActiveAdapterId);
        }
    }

    [Fact]
    public void DisconnectedAndChangedAdapterCannotKeepAnOldVerifiedState()
    {
        var tracker = new PathHealthTracker();
        var now = DateTimeOffset.UtcNow;
        Assert.False(tracker.Update(Sample("wifi", true, now), 2, 2, false).Online);
        tracker.Update(Sample("wifi", true, now), 2, 2, true);
        tracker.Forget("wifi");
        Assert.False(tracker.Update(Sample("wifi", true, now), 2, 2, true).Online);
        Assert.True(tracker.Update(Sample("wifi", true, now.AddSeconds(1)), 2, 2, true).Online);
    }

    [Fact]
    public void AllOfflineClearsActiveAndARejectedRouteDecisionCanRetry()
    {
        var c = new FailoverController(new());
        var now = DateTimeOffset.UtcNow;
        c.Evaluate([Sample("ethernet", true, now)]);
        Assert.Null(c.Evaluate([Sample("ethernet", false, now)]).ActiveAdapterId);
        c.Synchronize("ethernet");
        Assert.Equal("wifi", c.Evaluate([Sample("ethernet", false, now), Sample("wifi", true, now)]).ActiveAdapterId);
        c.Synchronize("ethernet"); // Windows rejected the first route change.
        Assert.True(c.Evaluate([Sample("ethernet", false, now), Sample("wifi", true, now)]).Changed);
    }

    [Fact]
    public void ReorderGapExpiresWithoutRequiringAnotherPacket()
    {
        var now = DateTimeOffset.UtcNow;
        var buffer = new PacketReorderBuffer(TimeSpan.FromMilliseconds(25));
        buffer.Add(1, new byte[] { 1 }, now);
        Assert.Empty(buffer.Add(3, new byte[] { 3 }, now));
        Assert.Empty(buffer.Flush(now.AddMilliseconds(24)));
        Assert.Equal(3, buffer.Flush(now.AddMilliseconds(25)).Single().Span[0]);
        Assert.Empty(buffer.Add(2, new byte[] { 2 }, now.AddMilliseconds(30)));
    }

    [Fact]
    public void PreparedConfigIsIdempotentAndKeepsOnePeerAndOneKeepalive()
    {
        const string input = "[Interface]\nAddress = 10.2.0.2/32\n[Peer]\nAllowedIPs = 0.0.0.0/0, ::/0\nEndpoint = vpn.example:51820\n";
        var result = WireGuardConfigRewriter.Prepare(input, IPAddress.Parse("203.0.113.5"), 51820);
        Assert.Equal(result, WireGuardConfigRewriter.Prepare(result, IPAddress.Parse("203.0.113.5"), 51820));
        Assert.Contains("PersistentKeepalive = 2", result);
        Assert.Contains("AllowedIPs = 0.0.0.0/1, 128.0.0.0/1", result);
        Assert.Throws<InvalidOperationException>(() => WireGuardConfigRewriter.Prepare(input + "[Peer]\n", IPAddress.Loopback, 443));
    }

    [Fact]
    public void NativeRouteStructuresMatchWindowsX64Abi()
    {
        Assert.Equal(104, Marshal.SizeOf<WindowsRouteTable.Route>());
        Assert.Equal(168, Marshal.SizeOf<WindowsRouteTable.Interface>());
        Assert.Equal(84, Marshal.OffsetOf<WindowsRouteTable.Route>(nameof(WindowsRouteTable.Route.Metric)).ToInt32());
        Assert.Equal(148, Marshal.OffsetOf<WindowsRouteTable.Interface>(nameof(WindowsRouteTable.Interface.Metric)).ToInt32());
    }

    [Fact]
    public async Task AuthenticatedRelayRepliesRestoreAPathEvenWhenPublicTcpProbeFails()
    {
        using var relay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)relay.Client.LocalEndPoint!;
        var key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        await using var client = new DualPathBondingClient([new(1, "wifi", IPAddress.Loopback, 1)], endpoint, key);
        client.Start();
        var baseline = new[] { new BondingPathSample("wifi", false, 0, 0, 100, 10, 0, 0) };
        for (var cycle = 0; cycle < 10; cycle++)
        {
            if (cycle > 0)
            {
                await client.UpdatePathsAsync([]);
                await client.UpdatePathsAsync([new(1, "wifi", IPAddress.Loopback, 1)]);
            }
            await client.ProbeAllAsync(CancellationToken.None);
            var received = await relay.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(BondingPacketCodec.TryDecode(received.Buffer, key, out var packet));
            var reply = BondingPacketCodec.Encode(packet! with { Kind = BondingPacketKind.ProbeReply,
                Direction = BondingDirection.Downlink, Sequence = (ulong)(cycle + 1) }, key);
            await relay.SendAsync(reply, received.RemoteEndPoint);
            var limit = DateTimeOffset.UtcNow.AddSeconds(2);
            while (!client.GetAdaptiveSamples(baseline).Single().Online && DateTimeOffset.UtcNow < limit) await Task.Delay(10);
            Assert.True(client.GetAdaptiveSamples(baseline).Single().Online, $"Relay path did not recover on reconnect cycle {cycle}: {client.GetAdaptiveSamples(baseline).Single()}");
        }
    }
}
