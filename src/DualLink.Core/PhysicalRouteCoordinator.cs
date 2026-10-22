using System.ComponentModel;
using System.Net.NetworkInformation;

namespace DualLink.Core;

public sealed class PhysicalRouteCoordinator(Action<int, int> setMetric,
    Func<int, uint?>? defaultMetric = null, Func<int?>? winningInterface = null)
{
    public bool Apply(IReadOnlyCollection<AdapterInfo> adapters, string preferredId,
        int preferredMetric, int backupMetric, Action<AdapterInfo, Exception>? standbyError = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preferredMetric);
        ArgumentOutOfRangeException.ThrowIfNegative(backupMetric);
        var selected = adapters.FirstOrDefault(x =>
            string.Equals(x.Id, preferredId, StringComparison.OrdinalIgnoreCase) && IsUsable(x));
        if (selected is null) return false;

        var selectedDefault = defaultMetric is null ? 0u : defaultMetric(selected.InterfaceIndex);
        if (selectedDefault is null) return false;
        var selectedCost = (long)selectedDefault.Value + preferredMetric;
        var backupPolicies = new List<(AdapterInfo Adapter, int Metric)>();
        foreach (var backup in adapters.Where(x =>
                     !string.Equals(x.Id, preferredId, StringComparison.OrdinalIgnoreCase) && IsUsable(x)))
        {
            var baseMetric = defaultMetric is null ? 0u : defaultMetric(backup.InterfaceIndex);
            // Windows compares route metric PLUS interface metric. Fixed 10/60
            // values do not select Ethernet when DHCP gave its /0 route 256 and
            // Wi-Fi's /0 route 0. Preserve gateway routes and adjust only the NIC
            // policy, making every available backup strictly more expensive.
            var metric = baseMetric is null ? backupMetric :
                Math.Max(backupMetric, selectedCost + 1 - baseMetric.Value);
            // Do not wrap a large route metric into a negative native argument
            // or demote the working path when this policy cannot be represented.
            if (metric > int.MaxValue) return false;
            backupPolicies.Add((backup, (int)metric));
        }

        // A failed promotion must leave the existing backup policy intact. A
        // disappearing standby must not prevent promoting the selected adapter
        // or updating the remaining adapters.
        setMetric(selected.InterfaceIndex, preferredMetric);
        foreach (var (backup, metric) in backupPolicies)
        {
            try { setMetric(backup.InterfaceIndex, metric); }
            catch (Win32Exception error) { standbyError?.Invoke(backup, error); }
        }
        return winningInterface is null || winningInterface() == selected.InterfaceIndex;
    }

    private static bool IsUsable(AdapterInfo adapter) => adapter.Status == OperationalStatus.Up &&
        adapter.Address is not null && adapter.Gateway is not null && adapter.InterfaceIndex > 0;
}
