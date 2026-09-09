namespace DualLink.Core;

/// <summary>
/// Rejects one-off ICMP loss without hiding a sustained path failure. A known
/// healthy path needs two failed rounds to go offline, and a failed path needs
/// two successful rounds before it is restored.
/// </summary>
public sealed class ProbeStabilizer
{
    private readonly Dictionary<string, ProbeHealth> _states = new(StringComparer.OrdinalIgnoreCase);

    public ProbeResult Filter(ProbeResult sample)
    {
        if (!_states.TryGetValue(sample.AdapterId, out var state))
        {
            state = new ProbeHealth { Online = sample.Online, LastGood = sample.Online ? sample : null };
            _states[sample.AdapterId] = state;
            return sample;
        }

        if (sample.Online)
        {
            state.ConsecutiveFailures = 0;
            state.ConsecutiveSuccesses++;
            state.LastGood = sample;
            if (!state.Online && state.ConsecutiveSuccesses < 2)
                return sample with { Online = false, Score = 0, Error = "Confirming recovered path" };
            state.Online = true;
            return sample;
        }

        state.ConsecutiveSuccesses = 0;
        state.ConsecutiveFailures++;
        if (state.Online && state.ConsecutiveFailures < 2 && state.LastGood is { } lastGood)
        {
            return lastGood with
            {
                Timestamp = sample.Timestamp,
                PacketLossPercent = Math.Max(50, lastGood.PacketLossPercent),
                Score = Math.Min(lastGood.Score, 25),
                Error = "Transient probe loss"
            };
        }

        state.Online = false;
        return sample;
    }

    private sealed class ProbeHealth
    {
        public bool Online { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int ConsecutiveSuccesses { get; set; }
        public ProbeResult? LastGood { get; set; }
    }
}
