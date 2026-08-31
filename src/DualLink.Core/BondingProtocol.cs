using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DualLink.Core;

public enum BondingPacketKind : byte { Data = 1, Probe = 2, ProbeReply = 3, Ack = 4 }
public enum BondingDirection : byte { Uplink = 1, Downlink = 2 }

public sealed record BondingPacket(
    BondingPacketKind Kind,
    byte PathId,
    ulong SessionId,
    ulong Sequence,
    ulong Acknowledgement,
    ReadOnlyMemory<byte> Payload,
    BondingDirection Direction = BondingDirection.Uplink);

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
        span[3] = (byte)packet.Direction;
        BinaryPrimitives.WriteUInt64BigEndian(span[4..12], packet.SessionId);
        BinaryPrimitives.WriteUInt64BigEndian(span[12..20], packet.Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(span[20..28], packet.Acknowledgement);
        BinaryPrimitives.WriteUInt32BigEndian(span[28..32], (uint)packet.Payload.Length);
        var sessionKey = DeriveSessionKey(key, packet.SessionId);
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(nonce, packet.Direction, packet.PathId, packet.Kind, packet.Sequence);
        using var cipher = new AesGcm(sessionKey, TagSize);
        cipher.Encrypt(
            nonce,
            packet.Payload.Span,
            span.Slice(HeaderSize, packet.Payload.Length),
            span[(HeaderSize + packet.Payload.Length)..],
            span[..HeaderSize]);
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> key, out BondingPacket? packet)
    {
        packet = null;
        if (key.Length < 32 || frame.Length < HeaderSize + TagSize || frame[0] != Version) return false;

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(frame[28..32]);
        if (payloadLength > MaximumPayloadSize || frame.Length != HeaderSize + payloadLength + TagSize) return false;

        var kind = (BondingPacketKind)frame[1];
        var direction = (BondingDirection)frame[3];
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(direction)) return false;
        var sessionId = BinaryPrimitives.ReadUInt64BigEndian(frame[4..12]);
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(frame[12..20]);
        var plaintext = new byte[payloadLength];
        var sessionKey = DeriveSessionKey(key, sessionId);
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(nonce, direction, frame[2], kind, sequence);
        try
        {
            using var cipher = new AesGcm(sessionKey, TagSize);
            cipher.Decrypt(
                nonce,
                frame.Slice(HeaderSize, (int)payloadLength),
                frame[(HeaderSize + (int)payloadLength)..],
                plaintext,
                frame[..HeaderSize]);
        }
        catch (CryptographicException)
        {
            return false;
        }

        packet = new BondingPacket(
            kind,
            frame[2],
            sessionId,
            sequence,
            BinaryPrimitives.ReadUInt64BigEndian(frame[20..28]),
            plaintext,
            direction);
        return true;
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < 32) throw new ArgumentException("The bonding key must contain at least 32 bytes.", nameof(key));
    }

    private static byte[] DeriveSessionKey(ReadOnlySpan<byte> masterKey, ulong sessionId)
    {
        Span<byte> session = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(session, sessionId);
        return HMACSHA256.HashData(masterKey, session);
    }

    private static void BuildNonce(
        Span<byte> nonce,
        BondingDirection direction,
        byte pathId,
        BondingPacketKind kind,
        ulong sequence)
    {
        nonce[0] = (byte)direction;
        nonce[1] = pathId;
        nonce[2] = (byte)kind;
        nonce[3] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], sequence);
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
