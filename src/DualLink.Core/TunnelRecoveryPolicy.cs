namespace DualLink.Core;

// Physical Internet and VPN reachability are different facts. Repeated VPN
// failures permit another underlay, while cooldown prevents route oscillation.
public sealed class TunnelRecoveryPolicy
{
    private readonly Dictionary<string, DateTimeOffset> _rejectedUntil = new(StringComparer.OrdinalIgnoreCase);
    private string? _identity;
    private string? _adapter;
    private int _failures;

    public bool Observe(string adapterId, string identity, bool verified)
    {
        if (_identity != identity) { _identity = identity; _adapter = adapterId; _failures = 0; }
        if (verified)
        {
            _failures = 0;
            _rejectedUntil.Remove(adapterId);
            return false;
        }
        return ++_failures >= 2;
    }

    public void Reject(string adapterId, DateTimeOffset now) => _rejectedUntil[adapterId] = now.AddSeconds(15);
    public bool IsEligible(string adapterId, DateTimeOffset now) =>
        !_rejectedUntil.TryGetValue(adapterId, out var deadline) || deadline <= now;
    public void Forget(string adapterId)
    {
        _rejectedUntil.Remove(adapterId);
        if (string.Equals(adapterId, _adapter, StringComparison.OrdinalIgnoreCase)) { _identity = null; _failures = 0; }
    }
}
