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
        Assert.Equal(original.Payload.ToArray(), decoded.Payload.ToArray());
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
            new BondingPacket(BondingPacketKind.Probe, 1, 7, 9, 0, []), Key);
        var wrongKey = Enumerable.Repeat((byte)0xff, 32).ToArray();

        Assert.False(BondingPacketCodec.TryDecode(encoded, wrongKey, out _));
    }
}
