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
