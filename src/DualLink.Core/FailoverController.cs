namespace DualLink.Core;

public sealed class FailoverController(DualLinkSettings settings)
{
    private string? _active;
    private string? _candidate;
    private int _candidateWins;

    public void Synchronize(string? routedAdapterId)
    {
        if (string.Equals(_active, routedAdapterId, StringComparison.OrdinalIgnoreCase)) return;
        _active = routedAdapterId;
        ResetCandidate();
    }

    public FailoverDecision Evaluate(IReadOnlyCollection<ProbeResult> probes, string? preferredId = null)
    {
        var online = probes.Where(x => x.Online).OrderByDescending(x => x.Score).ToList();
        if (online.Count == 0)
        {
            var changed = _active is not null;
            _active = null;
            ResetCandidate();
            return new(null, changed, "No working connection detected");
        }

        var preferred = online.FirstOrDefault(x => string.Equals(x.AdapterId, preferredId, StringComparison.OrdinalIgnoreCase));
        var best = preferred ?? online[0];
        var activeProbe = online.FirstOrDefault(x => x.AdapterId == _active);
        if (_active is null || activeProbe is null)
        {
            var old = _active;
            _active = best.AdapterId;
            ResetCandidate();
            return new(_active, old != _active, old is null ? "Initial best connection" : "Active connection failed");
        }

        if (preferred is not null && best.AdapterId != _active)
        {
            _active = best.AdapterId;
            ResetCandidate();
            return new(_active, true, "Preferred connection recovered and confirmed");
        }

        if (best.AdapterId == _active || best.Score < activeProbe.Score + settings.MinimumScoreImprovement)
        {
            ResetCandidate();
            return new(_active, false, "Active connection remains stable");
        }

        if (_candidate != best.AdapterId)
        {
            _candidate = best.AdapterId;
            _candidateWins = 1;
        }
        else
        {
            _candidateWins++;
        }

        if (_candidateWins < settings.SwitchConfirmationCount)
            return new(_active, false, $"Verifying better connection ({_candidateWins}/{settings.SwitchConfirmationCount})");

        _active = best.AdapterId;
        ResetCandidate();
        return new(_active, true, "Switched to a consistently better connection");
    }

    private void ResetCandidate()
    {
        _candidate = null;
        _candidateWins = 0;
    }
}

public sealed class PathHealthTracker
{
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public ProbeResult Update(ProbeResult sample, int failureConfirmations, int recoveryConfirmations,
        bool physicalLinkUp, TimeSpan? recoveryHold = null)
    {
        if (!physicalLinkUp) sample = sample with { Online = false, Score = 0, PacketLossPercent = 100 };
        failureConfirmations = Math.Max(1, failureConfirmations);
        recoveryConfirmations = Math.Max(1, recoveryConfirmations);
        if (!_states.TryGetValue(sample.AdapterId, out var state))
        {
            state = new State(sample.Online, sample.Online ? sample : null);
            _states[sample.AdapterId] = state;
            return sample;
        }

        if (!physicalLinkUp)
        {
            state.Online = false;
            state.Failures = failureConfirmations;
            state.Successes = 0;
            state.RecoveryStartedAt = null;
            return sample with { Online = false };
        }

        if (sample.Online)
        {
            state.LastGood = sample;
            state.Failures = 0;
            state.Successes++;
            state.RecoveryStartedAt ??= sample.Timestamp;
            if (state.Online || (state.Successes >= recoveryConfirmations &&
                sample.Timestamp - state.RecoveryStartedAt.Value >= (recoveryHold ?? TimeSpan.Zero)))
            {
                state.Online = true;
                return sample;
            }
            return sample with { Online = false, Score = 0, Error = $"Internet recovery confirmation {state.Successes}/{recoveryConfirmations}" };
        }

        state.Successes = 0;
        state.RecoveryStartedAt = null;
        state.Failures++;
        if (!state.Online || state.Failures >= failureConfirmations)
        {
            state.Online = false;
            return sample;
        }

        var lastGood = state.LastGood ?? sample;
        return lastGood with
        {
            Timestamp = sample.Timestamp,
            Online = true,
            Error = $"Transient probe failure {state.Failures}/{failureConfirmations}"
        };
    }

    public void Reset() => _states.Clear();
    public void Forget(string adapterId) => _states[adapterId] = new(false, null);

    private sealed class State(bool online, ProbeResult? lastGood)
    {
        public bool Online { get; set; } = online;
        public int Failures { get; set; }
        public int Successes { get; set; }
        public ProbeResult? LastGood { get; set; } = lastGood;
        public DateTimeOffset? RecoveryStartedAt { get; set; }
    }
}
