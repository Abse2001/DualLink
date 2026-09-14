namespace DualLink.Core;

public sealed class FailoverController(DualLinkSettings settings)
{
    private string? _active;
    private string? _candidate;
    private int _candidateWins;

    public FailoverDecision Evaluate(IReadOnlyCollection<ProbeResult> probes)
    {
        var online = probes.Where(x => x.Online).OrderByDescending(x => x.Score).ToList();
        if (online.Count == 0)
            return new(_active, false, "No working connection detected");

        var best = online[0];
        var activeProbe = online.FirstOrDefault(x => x.AdapterId == _active);
        if (_active is null || activeProbe is null)
        {
            var old = _active;
            _active = best.AdapterId;
            ResetCandidate();
            return new(_active, old != _active, old is null ? "Initial best connection" : "Active connection failed");
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
        bool physicalLinkUp)
    {
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
            return sample with { Online = false };
        }

        if (sample.Online)
        {
            state.LastGood = sample;
            state.Failures = 0;
            state.Successes++;
            if (state.Online || state.Successes >= recoveryConfirmations)
            {
                state.Online = true;
                return sample;
            }
            return sample with { Online = false, Error = $"Internet recovery confirmation {state.Successes}/{recoveryConfirmations}" };
        }

        state.Successes = 0;
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

    private sealed class State(bool online, ProbeResult? lastGood)
    {
        public bool Online { get; set; } = online;
        public int Failures { get; set; }
        public int Successes { get; set; }
        public ProbeResult? LastGood { get; set; } = lastGood;
    }
}
