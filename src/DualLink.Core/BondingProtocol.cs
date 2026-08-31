using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DualLink.Core;

public enum BondingPacketKind : byte { Data = 1, Probe = 2, ProbeReply = 3, Ack = 4 }

public sealed record BondingPacket(
    BondingPacketKind Kind,
    byte PathId,
    ulong SessionId,
    ulong Sequence,
    ulong Acknowledgement,
    ReadOnlyMemory<byte> Payload);

public static class BondingPacketCodec
{
    public const byte Version = 1;
    public const int HeaderSize = 32;
    public const int TagSize = 16;
    public const int MaximumPayloadSize = 1_400;

    public static byte[] Encode(BondingPacket packet, ReadOnlySpan<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(packet.Payload.Length, MaximumPayloadSize);
        ValidateKey(key);

        var frame = new byte[HeaderSize + packet.Payload.Length + TagSize];
        var span = frame.AsSpan();
        span[0] = Version;
        span[1] = (byte)packet.Kind;
        span[2] = packet.PathId;
        span[3] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(span[4..12], packet.SessionId);
        BinaryPrimitives.WriteUInt64BigEndian(span[12..20], packet.Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(span[20..28], packet.Acknowledgement);
        BinaryPrimitives.WriteUInt32BigEndian(span[28..32], (uint)packet.Payload.Length);
        packet.Payload.Span.CopyTo(span[HeaderSize..]);

        var authenticatedLength = HeaderSize + packet.Payload.Length;
        Span<byte> fullTag = stackalloc byte[32];
        HMACSHA256.HashData(key, span[..authenticatedLength], fullTag);
        fullTag[..TagSize].CopyTo(span[authenticatedLength..]);
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> key, out BondingPacket? packet)
    {
        packet = null;
        if (key.Length < 32 || frame.Length < HeaderSize + TagSize || frame[0] != Version) return false;

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(frame[28..32]);
        if (payloadLength > MaximumPayloadSize || frame.Length != HeaderSize + payloadLength + TagSize) return false;

        var authenticatedLength = HeaderSize + (int)payloadLength;
        Span<byte> fullTag = stackalloc byte[32];
        HMACSHA256.HashData(key, frame[..authenticatedLength], fullTag);
        if (!CryptographicOperations.FixedTimeEquals(fullTag[..TagSize], frame[authenticatedLength..])) return false;

        var kind = (BondingPacketKind)frame[1];
        if (!Enum.IsDefined(kind)) return false;
        packet = new BondingPacket(
            kind,
            frame[2],
            BinaryPrimitives.ReadUInt64BigEndian(frame[4..12]),
            BinaryPrimitives.ReadUInt64BigEndian(frame[12..20]),
            BinaryPrimitives.ReadUInt64BigEndian(frame[20..28]),
            frame.Slice(HeaderSize, (int)payloadLength).ToArray());
        return true;
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < 32) throw new ArgumentException("The bonding key must contain at least 32 bytes.", nameof(key));
    }
}

public sealed class ReplayWindow(int windowSize = 4096)
{
    private readonly HashSet<ulong> _seen = [];
    private ulong _highest;
    private bool _initialized;

    public bool TryAccept(ulong sequence)
    {
        if (!_initialized)
        {
            _initialized = true;
            _highest = sequence;
            _seen.Add(sequence);
            return true;
        }

        if (sequence > _highest)
        {
            _highest = sequence;
            _seen.RemoveWhere(value => _highest - value >= (ulong)windowSize);
            return _seen.Add(sequence);
        }

        if (_highest - sequence >= (ulong)windowSize) return false;
        return _seen.Add(sequence);
    }
}
