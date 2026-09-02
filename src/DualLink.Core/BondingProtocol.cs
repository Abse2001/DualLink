using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DualLink.Core;

public enum BondingPacketKind : byte { Data = 1, Probe = 2, ProbeReply = 3, Ack = 4, Control = 5 }
public enum BondingDirection : byte { Uplink = 1, Downlink = 2 }

public sealed record BondingPacket(
    BondingPacketKind Kind,
    byte PathId,
    ulong SessionId,
    ulong Sequence,
    ulong Acknowledgement,
    ReadOnlyMemory<byte> Payload,
    BondingDirection Direction = BondingDirection.Uplink);

public sealed record BondingControlPath(byte PathId, bool Online, double RttMs, double JitterMs, double LossPercent, double DeliveryRateMbps);
public sealed record BondingControl(BondingMode Mode, byte PreferredPathId, IReadOnlyList<BondingControlPath> Paths);

public static class BondingControlCodec
{
    private const byte ControlVersion = 1;
    private const int PathSize = 10;

    public static byte[] Encode(BondingControl control)
    {
        if (control.Paths.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(control));
        var data = new byte[4 + control.Paths.Count * PathSize];
        data[0] = ControlVersion;
        data[1] = (byte)control.Mode;
        data[2] = control.PreferredPathId;
        data[3] = (byte)control.Paths.Count;
        for (var index = 0; index < control.Paths.Count; index++)
        {
            var path = control.Paths[index];
            var offset = 4 + index * PathSize;
            data[offset] = path.PathId;
            data[offset + 1] = path.Online ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 2, 2), Scale(path.RttMs, 1));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 4, 2), Scale(path.JitterMs, 1));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 6, 2), Scale(path.LossPercent, 100));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 8, 2), Scale(path.DeliveryRateMbps, 10));
        }
        return data;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out BondingControl? control)
    {
        control = null;
        if (data.Length < 4 || data[0] != ControlVersion || !Enum.IsDefined((BondingMode)data[1])) return false;
        var count = data[3];
        if (data.Length != 4 + count * PathSize) return false;
        var paths = new List<BondingControlPath>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = 4 + index * PathSize;
            paths.Add(new(data[offset], data[offset + 1] != 0,
                BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 4, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 6, 2)) / 100d,
                BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 8, 2)) / 10d));
        }
        control = new((BondingMode)data[1], data[2], paths);
        return true;
    }

    private static ushort Scale(double value, double scale) =>
        (ushort)Math.Clamp(Math.Round(Math.Max(0, value) * scale), 0, ushort.MaxValue);
}

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
