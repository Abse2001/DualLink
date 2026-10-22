namespace DualLink.Core;

// Physical Internet and VPN reachability are different facts. Repeated VPN
// failures permit another underlay, while cooldown prevents route oscillation.
public sealed class TunnelRecoveryPolicy
{
    private readonly Dictionary<string, DateTimeOffset> _rejectedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _settlingPeriod;
    private string? _identity;
    private string? _adapter;
    private int _failures;
    private DateTimeOffset? _settlingUntil;

    public TunnelRecoveryPolicy(TimeSpan? settlingPeriod = null)
    {
        _settlingPeriod = settlingPeriod ?? TimeSpan.FromSeconds(6);
        if (_settlingPeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(settlingPeriod));
    }

    // Call only after an actual route move/repair or adapter epoch change. A
    // successful read-only route audit must not continually extend this grace.
    public void RouteChanged(string adapterId, string identity, DateTimeOffset now)
    {
        _adapter = adapterId;
        _identity = identity;
        _failures = 0;
        _settlingUntil = now + _settlingPeriod;
    }

    public bool Observe(string adapterId, string identity, bool verified, DateTimeOffset? now = null)
    {
        if (!string.Equals(_adapter, adapterId, StringComparison.OrdinalIgnoreCase) || _identity != identity)
        {
            _identity = identity;
            _adapter = adapterId;
            _failures = 0;
            _settlingUntil = null;
        }
        if (verified)
        {
            _failures = 0;
            _settlingUntil = null;
            _rejectedUntil.Remove(adapterId);
            return false;
        }
        // Physical link failure is handled by path selection independently of
        // this grace. Only failed end-to-end VPN checks wait for roaming.
        if (_settlingUntil is { } deadline && (now ?? DateTimeOffset.UtcNow) < deadline)
            return false;
        return ++_failures >= 2;
    }

    public void Reject(string adapterId, DateTimeOffset now) => _rejectedUntil[adapterId] = now.AddSeconds(15);
    public bool IsEligible(string adapterId, DateTimeOffset now) =>
        !_rejectedUntil.TryGetValue(adapterId, out var deadline) || deadline <= now;

    public IReadOnlyCollection<ProbeResult> EligibleProbes(IReadOnlyCollection<ProbeResult> probes, DateTimeOffset now)
    {
        var online = probes.Where(x => x.Online).ToArray();
        var eligible = online.Where(x => IsEligible(x.AdapterId, now)).ToArray();
        // Cooldown prevents oscillation while another working path exists. It
        // must never make the only working Internet adapter unavailable.
        return eligible.Length > 0 ? eligible : online;
    }

    public void Forget(string adapterId)
    {
        _rejectedUntil.Remove(adapterId);
        if (string.Equals(adapterId, _adapter, StringComparison.OrdinalIgnoreCase))
        {
            _identity = null;
            _adapter = null;
            _failures = 0;
            _settlingUntil = null;
        }
    }
}
