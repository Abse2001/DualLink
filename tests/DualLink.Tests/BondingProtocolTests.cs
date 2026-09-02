using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public sealed class BondingProtocolTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void PacketRoundTrips()
    {
        var original = new BondingPacket(BondingPacketKind.Data, 2, 42, 100, 91, "hello"u8.ToArray());
        var encoded = BondingPacketCodec.Encode(original, Key);

        Assert.True(BondingPacketCodec.TryDecode(encoded, Key, out var decoded));
        Assert.Equal(original.Kind, decoded!.Kind);
        Assert.Equal(original.PathId, decoded.PathId);
        Assert.Equal(original.SessionId, decoded.SessionId);
        Assert.Equal(original.Sequence, decoded.Sequence);
        Assert.Equal(original.Acknowledgement, decoded.Acknowledgement);
        Assert.Equal(original.Direction, decoded.Direction);
        Assert.Equal(original.Payload.ToArray(), decoded.Payload.ToArray());
        Assert.Equal(-1, encoded.AsSpan().IndexOf("hello"u8));
    }

    [Fact]
    public void ModifiedPacketIsRejected()
    {
        var encoded = BondingPacketCodec.Encode(
            new BondingPacket(BondingPacketKind.Data, 1, 7, 9, 0, "payload"u8.ToArray()), Key);
        encoded[BondingPacketCodec.HeaderSize] ^= 1;

        Assert.False(BondingPacketCodec.TryDecode(encoded, Key, out _));
    }

    [Fact]
    public void WrongKeyIsRejected()
    {
        var encoded = BondingPacketCodec.Encode(
            new BondingPacket(BondingPacketKind.Probe, 1, 7, 9, 0, Array.Empty<byte>()), Key);
        var wrongKey = Enumerable.Repeat((byte)0xff, 32).ToArray();

        Assert.False(BondingPacketCodec.TryDecode(encoded, wrongKey, out _));
    }

    [Fact]
    public void ControlMessageRoundTripsPathTelemetry()
    {
        var original = new BondingControl(BondingMode.Failover, 2,
        [
            new BondingControlPath(1, true, 31, 2, .25, 20.4),
            new BondingControlPath(2, false, 64, 9, 3.5, 0)
        ]);

        var encoded = BondingControlCodec.Encode(original);

        Assert.True(BondingControlCodec.TryDecode(encoded, out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(BondingMode.Failover, decoded!.Mode);
        Assert.Equal(2, decoded.PreferredPathId);
        Assert.Equal(2, decoded.Paths.Count);
        Assert.Equal(20.4, decoded.Paths[0].DeliveryRateMbps, 1);
        Assert.Equal(.25, decoded.Paths[0].LossPercent, 2);
        Assert.False(decoded.Paths[1].Online);
    }

    [Fact]
    public void InvalidControlMessageIsRejected()
    {
        Assert.False(BondingControlCodec.TryDecode([1, 255, 0, 0], out _));
        Assert.False(BondingControlCodec.TryDecode([1, 0, 0, 1], out _));
    }

    [Fact]
    public void ReplayWindowRejectsDuplicatesAndExpiredSequences()
    {
        var window = new ReplayWindow(4);
        Assert.True(window.TryAccept(10));
        Assert.False(window.TryAccept(10));
        Assert.True(window.TryAccept(12));
        Assert.True(window.TryAccept(9));
        Assert.True(window.TryAccept(15));
        Assert.False(window.TryAccept(10));
    }
}
