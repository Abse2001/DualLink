namespace DualLink.Core;

public enum BondingMode { Bonding, Failover, LoadBalancing, Redundant }

public sealed record BondingPathSample(
    string PathId,
    bool Online,
    double SmoothedRttMs,
    double JitterMs,
    double LossPercent,
    double DeliveryRateMbps,
    long QueuedBytes,
    double Reliability);

/// <summary>
/// Chooses the path with the earliest predicted delivery time while preserving
/// throughput-proportional use of healthy links with similar latency.
/// </summary>
public sealed class AdaptiveBondingScheduler
{
    private readonly Dictionary<string, double> _virtualFinishMs = [];

    public string? SelectPath(IReadOnlyCollection<BondingPathSample> paths, int packetBytes, BondingMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(packetBytes);

        var healthy = paths.Where(IsUsable).ToList();
        if (healthy.Count == 0) return null;

        if (mode == BondingMode.Failover)
            return healthy.OrderBy(PathCost).First().PathId;

        var selected = healthy.MinBy(path => PredictedArrival(path, packetBytes));
        if (selected is null) return null;

        var serializationMs = packetBytes * 8d / (selected.DeliveryRateMbps * 1_000d);
        _virtualFinishMs[selected.PathId] = Math.Max(
            _virtualFinishMs.GetValueOrDefault(selected.PathId),
            PathCost(selected)) + serializationMs;
        return selected.PathId;
    }

    public void Forget(string pathId) => _virtualFinishMs.Remove(pathId);

    private double PredictedArrival(BondingPathSample path, int packetBytes)
    {
        var queuedMs = path.QueuedBytes * 8d / (path.DeliveryRateMbps * 1_000d);
        var serializationMs = packetBytes * 8d / (path.DeliveryRateMbps * 1_000d);
        var lossPenalty = path.SmoothedRttMs * Math.Clamp(path.LossPercent / 100d, 0, 1) * 2;
        var reliabilityPenalty = (1 - Math.Clamp(path.Reliability, 0, 1)) * path.SmoothedRttMs;
        var baseArrival = PathCost(path) + queuedMs + serializationMs + lossPenalty + reliabilityPenalty;
        return Math.Max(baseArrival, _virtualFinishMs.GetValueOrDefault(path.PathId));
    }

    private static bool IsUsable(BondingPathSample path) =>
        path.Online && path.DeliveryRateMbps > 0 && path.Reliability > 0.05;

    private static double PathCost(BondingPathSample path) =>
        path.SmoothedRttMs / 2d + path.JitterMs;
}

public sealed class PacketReorderBuffer(TimeSpan maximumHold, int maximumPackets = 4096)
{
    private readonly SortedDictionary<ulong, BufferedPacket> _packets = [];
    private ulong _nextSequence;
    private bool _initialized;

    public IReadOnlyList<ReadOnlyMemory<byte>> Add(ulong sequence, ReadOnlyMemory<byte> payload, DateTimeOffset now)
    {
        if (!_initialized)
        {
            _nextSequence = sequence;
            _initialized = true;
        }

        if (sequence < _nextSequence || _packets.ContainsKey(sequence)) return [];
        _packets[sequence] = new(payload.ToArray(), now);
        return Drain(now);
    }

    private IReadOnlyList<ReadOnlyMemory<byte>> Drain(DateTimeOffset now)
    {
        var output = new List<ReadOnlyMemory<byte>>();
        while (_packets.Count > 0)
        {
            if (_packets.Remove(_nextSequence, out var packet))
            {
                output.Add(packet.Payload);
                _nextSequence++;
                continue;
            }

            var first = _packets.First();
            var expired = now - first.Value.ReceivedAt >= maximumHold;
            if (!expired && _packets.Count < maximumPackets) break;
            _nextSequence = first.Key;
        }
        return output;
    }

    private sealed record BufferedPacket(ReadOnlyMemory<byte> Payload, DateTimeOffset ReceivedAt);
}
