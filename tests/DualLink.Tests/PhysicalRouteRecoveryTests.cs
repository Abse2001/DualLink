using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public sealed class PhysicalRouteRecoveryTests
{
    private static AdapterInfo Adapter(string id, int index) => new(id, id, id,
        NetworkInterfaceType.Ethernet, OperationalStatus.Up, index,
        IPAddress.Parse($"192.0.2.{index}"), IPAddress.Parse("192.0.2.1"), null);

    [Theory]
    [InlineData("ethernet", 10, 20)]
    [InlineData("wifi", 20, 10)]
    public void HandoffPromotesRequestedAdapterBeforeDemotingTheCarryingAdapter(string target, int selectedIndex, int backupIndex)
    {
        var changes = new List<(int Index, int Metric)>();
        var routes = new PhysicalRouteCoordinator((index, metric) => changes.Add((index, metric)));
        Assert.True(routes.Apply([Adapter("wifi", 20), Adapter("ethernet", 10)], target, 10, 60));
        Assert.Equal(new[] { (selectedIndex, 10), (backupIndex, 60) }, changes);
    }

    [Fact]
    public void RepeatedEthernetWifiAndUsbHandoffsKeepOnePreferredPolicy()
    {
        var metrics = new Dictionary<int, int>();
        var adapters = new[] { Adapter("ethernet", 10), Adapter("wifi", 20), Adapter("usb", 30) };
        var routes = new PhysicalRouteCoordinator((index, metric) => metrics[index] = metric);
        for (var cycle = 0; cycle < 100; cycle++)
        {
            foreach (var selected in adapters)
            {
                Assert.True(routes.Apply(adapters, selected.Id, 10, 60));
                Assert.Equal(selected.InterfaceIndex, metrics.Single(x => x.Value == 10).Key);
                Assert.All(metrics.Where(x => x.Key != selected.InterfaceIndex), x => Assert.Equal(60, x.Value));
            }
        }
    }

    [Fact]
    public void AdapterReconnectionUsesItsNewInterfaceIndexWithTheSameSavedPreference()
    {
        var changes = new List<(int Index, int Metric)>();
        var routes = new PhysicalRouteCoordinator((index, metric) => changes.Add((index, metric)));
        Assert.True(routes.Apply([Adapter("ethernet", 10), Adapter("wifi", 20)], "ethernet", 10, 60));
        changes.Clear();
        Assert.True(routes.Apply([Adapter("ethernet", 40), Adapter("wifi", 20)], "ETHERNET", 10, 60));
        Assert.Equal(new[] { (40, 10), (20, 60) }, changes);
    }

    [Fact]
    public void SelectedMetricFailureLeavesThePreviousBackupPolicyIntactAndCanRetry()
    {
        var metrics = new Dictionary<int, int> { [10] = 60, [20] = 10 };
        var fail = true;
        var routes = new PhysicalRouteCoordinator((index, metric) =>
        {
            if (index == 10 && fail) throw new Win32Exception(1168);
            metrics[index] = metric;
        });
        var adapters = new[] { Adapter("ethernet", 10), Adapter("wifi", 20) };
        Assert.Throws<Win32Exception>(() => routes.Apply(adapters, "ethernet", 10, 60));
        Assert.Equal(60, metrics[10]);
        Assert.Equal(10, metrics[20]);
        fail = false;
        Assert.True(routes.Apply(adapters, "ethernet", 10, 60));
        Assert.Equal(10, metrics[10]);
        Assert.Equal(60, metrics[20]);
    }

    [Fact]
    public void DisappearingBackupDoesNotPreventSelectedPromotionOrThirdAdapterDemotion()
    {
        var metrics = new Dictionary<int, int>();
        var errors = new List<string>();
        var routes = new PhysicalRouteCoordinator((index, metric) =>
        {
            if (index == 20) throw new Win32Exception(1168);
            metrics[index] = metric;
        });
        Assert.True(routes.Apply([Adapter("wifi", 20), Adapter("usb", 30), Adapter("ethernet", 10)],
            "ethernet", 10, 60, (adapter, _) => errors.Add(adapter.Id)));
        Assert.Equal(10, metrics[10]);
        Assert.Equal(60, metrics[30]);
        Assert.Equal(new[] { "wifi" }, errors);
    }

    [Fact]
    public void MissingOrUnusableSelectionDoesNotDemoteTheOnlyWorkingAdapter()
    {
        var changes = new List<int>();
        var routes = new PhysicalRouteCoordinator((index, _) => changes.Add(index));
        Assert.False(routes.Apply([Adapter("wifi", 20)], "ethernet", 10, 60));
        Assert.False(routes.Apply([Adapter("wifi", 20), Adapter("ethernet", 10) with { Status = OperationalStatus.Down }],
            "ethernet", 10, 60));
        Assert.False(routes.Apply([Adapter("wifi", 20), Adapter("ethernet", 10) with { Gateway = null }],
            "ethernet", 10, 60));
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData("ethernet", 256u, 0u)]
    [InlineData("wifi", 0u, 256u)]
    public void UnequalDhcpDefaultRouteMetricsCannotOverrideTheSelectedAdapter(string target, uint ethernetBase, uint wifiBase)
    {
        var defaults = new Dictionary<int, uint> { [10] = ethernetBase, [20] = wifiBase, [30] = 500 };
        var metrics = new Dictionary<int, int> { [10] = 60, [20] = 10, [30] = 60 };
        int Winner() => defaults.OrderBy(row => (long)row.Value + metrics[row.Key]).First().Key;
        var routes = new PhysicalRouteCoordinator((index, metric) => metrics[index] = metric,
            index => defaults[index], () => Winner());
        var adapters = new[] { Adapter("ethernet", 10), Adapter("wifi", 20), Adapter("usb", 30) };
        for (var cycle = 0; cycle < 100; cycle++)
        {
            Assert.True(routes.Apply(adapters, target, 10, 60));
            Assert.Equal(target == "ethernet" ? 10 : 20, Winner());
            Assert.True(routes.Apply(adapters, target == "ethernet" ? "wifi" : "ethernet", 10, 60));
            Assert.Equal(target == "ethernet" ? 20 : 10, Winner());
        }
        Assert.Equal(ethernetBase, defaults[10]);
        Assert.Equal(wifiBase, defaults[20]);
    }

    [Fact]
    public void MissingSelectedDefaultRouteCannotBeReportedAsAnAppliedPreference()
    {
        var changes = new List<int>();
        var routes = new PhysicalRouteCoordinator((index, _) => changes.Add(index),
            index => index == 10 ? null : 0u, () => 20);
        Assert.False(routes.Apply([Adapter("ethernet", 10), Adapter("wifi", 20)], "ethernet", 10, 60));
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(20)]
    public void KernelRouteProofMustSelectTheRequestedInterface(int? actualWinner)
    {
        var routes = new PhysicalRouteCoordinator((_, _) => { }, _ => 0u, () => actualWinner);
        Assert.False(routes.Apply([Adapter("ethernet", 10), Adapter("wifi", 20)], "ethernet", 10, 60));
    }

    [Fact]
    public void FailedBackupDemotionCannotBeReportedAsSuccessfulWhenItStillWins()
    {
        var defaults = new Dictionary<int, uint> { [10] = 256, [20] = 0 };
        var metrics = new Dictionary<int, int> { [10] = 60, [20] = 10 };
        var routes = new PhysicalRouteCoordinator((index, metric) =>
        {
            if (index == 20) throw new Win32Exception(1168);
            metrics[index] = metric;
        }, index => defaults[index], () => defaults.OrderBy(row => (long)row.Value + metrics[row.Key]).First().Key);
        Assert.False(routes.Apply([Adapter("ethernet", 10), Adapter("wifi", 20)], "ethernet", 10, 60));
        Assert.Equal(10, metrics[20]);
    }

    [Fact]
    public void UnrepresentableMetricDifferenceDoesNotWrapOrDisturbTheWorkingRoute()
    {
        var changes = new List<int>();
        var routes = new PhysicalRouteCoordinator((index, _) => changes.Add(index),
            index => index == 10 ? uint.MaxValue : 0u, () => 20);
        Assert.False(routes.Apply([Adapter("ethernet", 10), Adapter("wifi", 20)], "ethernet", 10, 60));
        Assert.Empty(changes);
    }
}
