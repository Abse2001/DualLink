using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;

namespace DualLink.Core;

public sealed record WireGuardEndpointRoute(int InterfaceIndex, IPAddress Gateway, uint Metric, byte PrefixLength = 32);

// Keep the native Windows API behind this small seam so recovery can be tested
// against gateway changes and competing routes, without changing a test host's NICs.
public interface IWireGuardRouteTable
{
    void EnsureHost(IPAddress destination, int interfaceIndex, IPAddress gateway, int metric);
    void SetMetric(int interfaceIndex, int metric);
    WireGuardEndpointRoute? BestRoute(IPAddress destination);
}

public sealed class WireGuardRouteCoordinator(IWireGuardRouteTable routes)
{
    public bool Reconcile(IReadOnlyList<AdapterInfo> adapters, IPAddress endpoint, AdapterInfo selected,
        Action<AdapterInfo, Exception>? standbyError = null)
    {
        if (!IsUsable(selected)) return false;

        // Reconcile even when the winning interface has not changed: DHCP can
        // replace its gateway while an old /32 still points at the same ifIndex.
        // EnsureHost installs a new gateway before removing the obsolete row.
        routes.EnsureHost(endpoint, selected.InterfaceIndex, selected.Gateway!, 1);
        routes.SetMetric(selected.InterfaceIndex, 5);
        foreach (var backup in adapters.Where(x =>
                     !string.Equals(x.Id, selected.Id, StringComparison.OrdinalIgnoreCase) && IsUsable(x)))
        {
            try
            {
                routes.EnsureHost(endpoint, backup.InterfaceIndex, backup.Gateway!, 5000);
                routes.SetMetric(backup.InterfaceIndex, 500);
            }
            catch (Win32Exception error) { standbyError?.Invoke(backup, error); }
        }

        return IsCurrent(endpoint, selected);
    }

    public bool IsCurrent(IPAddress endpoint, AdapterInfo selected)
    {
        if (!IsUsable(selected)) return false;
        var best = routes.BestRoute(endpoint);
        return best is not null && best.InterfaceIndex == selected.InterfaceIndex &&
            best.Gateway.Equals(selected.Gateway) && best.Metric == 1 && best.PrefixLength == 32;
    }

    private static bool IsUsable(AdapterInfo adapter) => adapter.Status == OperationalStatus.Up &&
        adapter.Address is not null && adapter.Gateway is not null && adapter.InterfaceIndex > 0;
}
