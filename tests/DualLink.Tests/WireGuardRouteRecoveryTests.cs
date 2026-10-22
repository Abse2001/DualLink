using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public sealed class WireGuardRouteRecoveryTests
{
    private static readonly IPAddress Endpoint = IPAddress.Parse("203.0.113.9");

    private static AdapterInfo Adapter(string id, int index, string gateway, string address) =>
        new(id, id, id, id == "wifi" ? NetworkInterfaceType.Wireless80211 : NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, index, IPAddress.Parse(address), IPAddress.Parse(gateway), null);

    private static AdapterInfo Ethernet => Adapter("ethernet", 7, "192.168.1.1", "192.168.1.20");
    private static AdapterInfo Wifi => Adapter("wifi", 13, "192.168.2.1", "192.168.2.20");

    [Fact]
    public void RepeatedEthernetLossAndRecoveryDoesNotLeaveTheEndpointOnWifi()
    {
        var table = new FakeRoutes();
        var coordinator = new WireGuardRouteCoordinator(table);
        var ethernet = Ethernet;
        var wifi = Wifi;

        for (var cycle = 0; cycle < 100; cycle++)
        {
            Assert.True(coordinator.Reconcile([ethernet, wifi], Endpoint, ethernet));
            Assert.Equal(ethernet.InterfaceIndex, table.BestRoute(Endpoint)!.InterfaceIndex);

            var disconnected = ethernet with { Status = OperationalStatus.Down };
            table.SetAvailable(wifi.InterfaceIndex);
            Assert.True(coordinator.Reconcile([disconnected, wifi], Endpoint, wifi));
            Assert.Equal(wifi.InterfaceIndex, table.BestRoute(Endpoint)!.InterfaceIndex);

            table.SetAvailable(ethernet.InterfaceIndex, wifi.InterfaceIndex);
            Assert.True(coordinator.Reconcile([ethernet, wifi], Endpoint, ethernet));
            // Turning off the backup after failback must leave a usable endpoint route.
            table.SetAvailable(ethernet.InterfaceIndex);
            Assert.Equal(new(ethernet.InterfaceIndex, ethernet.Gateway!, 1), table.BestRoute(Endpoint));
            table.SetAvailable(ethernet.InterfaceIndex, wifi.InterfaceIndex);
        }
    }

    [Fact]
    public void SameInterfaceWithANewDhcpGatewayReplacesTheStaleHostRoute()
    {
        var table = new FakeRoutes();
        var coordinator = new WireGuardRouteCoordinator(table);
        Assert.True(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
        var renewed = Ethernet with { Gateway = IPAddress.Parse("192.168.1.254"), Address = IPAddress.Parse("192.168.1.30") };

        Assert.True(coordinator.Reconcile([renewed, Wifi], Endpoint, renewed));
        Assert.Equal(renewed.Gateway, table.BestRoute(Endpoint)!.Gateway);
        Assert.DoesNotContain(table.Hosts, x => x.Route.InterfaceIndex == renewed.InterfaceIndex && x.Route.Gateway.Equals(Ethernet.Gateway));
    }

    [Fact]
    public void RecreatedEthernetInterfaceCanReclaimTheEndpoint()
    {
        var table = new FakeRoutes();
        var coordinator = new WireGuardRouteCoordinator(table);
        Assert.True(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
        table.SetAvailable(Wifi.InterfaceIndex);
        Assert.True(coordinator.Reconcile([Wifi], Endpoint, Wifi));
        var replaced = Ethernet with { InterfaceIndex = 21, Address = IPAddress.Parse("192.168.1.45") };
        table.SetAvailable(replaced.InterfaceIndex, Wifi.InterfaceIndex);

        Assert.True(coordinator.Reconcile([replaced, Wifi], Endpoint, replaced));
        Assert.Equal(replaced.InterfaceIndex, table.BestRoute(Endpoint)!.InterfaceIndex);
    }

    [Fact]
    public void ReconciliationRepairsEndpointRoutesEvenIfTheSelectedAdapterDidNotChange()
    {
        var table = new FakeRoutes();
        var coordinator = new WireGuardRouteCoordinator(table);
        Assert.True(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
        table.EnsureHost(Endpoint, Ethernet.InterfaceIndex, Ethernet.Gateway!, 5000);
        table.EnsureHost(Endpoint, Wifi.InterfaceIndex, Wifi.Gateway!, 1);
        table.SetMetric(Wifi.InterfaceIndex, 5);
        Assert.Equal(Wifi.InterfaceIndex, table.BestRoute(Endpoint)!.InterfaceIndex);

        Assert.True(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
        Assert.Equal(Ethernet.InterfaceIndex, table.BestRoute(Endpoint)!.InterfaceIndex);
    }

    [Theory]
    [InlineData(13, "192.168.2.1", 1)]
    [InlineData(7, "192.168.1.254", 1)]
    [InlineData(7, "192.168.1.1", 5000)]
    public void ACompetingOrStaleWinningRouteCannotBeReportedAsSuccessful(int index, string gateway, uint metric)
    {
        var table = new FakeRoutes { BestOverride = new(index, IPAddress.Parse(gateway), metric) };
        Assert.False(new WireGuardRouteCoordinator(table).Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
        table.BestOverride = null;
        Assert.True(new WireGuardRouteCoordinator(table).Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
    }

    [Fact]
    public void MissingWinningRouteCannotBeReportedAsSuccessful()
    {
        var table = new FakeRoutes { SuppressBest = true };
        Assert.False(new WireGuardRouteCoordinator(table).Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("metric")]
    public void SelectedPathFailureKeepsTheWorkingBackupAndCanBeRetried(string operation)
    {
        var table = new FakeRoutes();
        var coordinator = new WireGuardRouteCoordinator(table);
        Assert.True(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Wifi));
        table.FailIndex = Ethernet.InterfaceIndex;
        table.FailOperation = operation;

        Assert.Throws<Win32Exception>(() => coordinator.Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
        Assert.Equal(Wifi.InterfaceIndex, table.BestRoute(Endpoint)!.InterfaceIndex);
        table.FailIndex = null;
        Assert.True(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
    }

    [Fact]
    public void StandbyFailureDoesNotDiscardAValidSelectedPath()
    {
        var table = new FakeRoutes { FailIndex = Wifi.InterfaceIndex, FailOperation = "host" };
        var errors = new List<string>();
        Assert.True(new WireGuardRouteCoordinator(table).Reconcile([Ethernet, Wifi], Endpoint, Ethernet,
            (adapter, _) => errors.Add(adapter.Id)));
        Assert.Equal(["wifi"], errors);
        Assert.Equal(Ethernet.InterfaceIndex, table.BestRoute(Endpoint)!.InterfaceIndex);
    }

    [Fact]
    public void FailedStandbyDemotionCannotHideThatItStillWinsTheEndpoint()
    {
        var table = new FakeRoutes();
        var coordinator = new WireGuardRouteCoordinator(table);
        Assert.True(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Ethernet));
        table.FailIndex = Ethernet.InterfaceIndex;
        table.FailOperation = "host";

        Assert.False(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Wifi));
        table.FailIndex = null;
        Assert.True(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Wifi));
    }

    [Theory]
    [InlineData(false, true, true, 7)]
    [InlineData(true, false, true, 7)]
    [InlineData(true, true, false, 7)]
    [InlineData(true, true, true, 0)]
    public void UnusableSelectedAdapterDoesNotMutateRoutes(bool up, bool address, bool gateway, int index)
    {
        var table = new FakeRoutes();
        var selected = Ethernet with
        {
            Status = up ? OperationalStatus.Up : OperationalStatus.Down,
            Address = address ? Ethernet.Address : null,
            Gateway = gateway ? Ethernet.Gateway : null,
            InterfaceIndex = index
        };
        Assert.False(new WireGuardRouteCoordinator(table).Reconcile([selected, Wifi], Endpoint, selected));
        Assert.Empty(table.Operations);
    }

    [Fact]
    public void PromotionHappensBeforeStandbyDemotionAndSkipsDisconnectedAdapters()
    {
        var table = new FakeRoutes();
        Assert.True(new WireGuardRouteCoordinator(table).Reconcile(
            [Ethernet, Wifi, Adapter("usb", 25, "192.168.3.1", "192.168.3.2") with { Status = OperationalStatus.Down }], Endpoint, Wifi));
        Assert.Equal(["host:13:1", "metric:13:5", "host:7:5000", "metric:7:500"], table.Operations);
    }

    [Fact]
    public void EndpointChangeGetsANewHostRouteWhileTheTunnelStaysSelected()
    {
        var table = new FakeRoutes();
        var coordinator = new WireGuardRouteCoordinator(table);
        Assert.True(coordinator.Reconcile([Ethernet, Wifi], Endpoint, Wifi));
        var replacement = IPAddress.Parse("198.51.100.17");
        Assert.True(coordinator.Reconcile([Ethernet, Wifi], replacement, Wifi));
        Assert.Equal(Wifi.InterfaceIndex, table.BestRoute(replacement)!.InterfaceIndex);
    }

    [Theory]
    [InlineData("Home VPN\tpeer-key\t203.0.113.1:51820\n", "Home VPN", "203.0.113.1")]
    [InlineData("Home VPN\tpeer-key\t203.0.113.1:443\r\n", "home vpn", "203.0.113.1")]
    [InlineData("old\tpeer-key\t198.51.100.1:443\nHome VPN\tpeer-key\t203.0.113.1:443\n", "Home VPN", "203.0.113.1")]
    [InlineData("Home VPN\tpeer-a\t203.0.113.1:443\nHome VPN\tpeer-b\t203.0.113.1:51820\n", "Home VPN", "203.0.113.1")]
    public void EndpointParserPreservesTunnelNamesAndFindsOneActiveIpv4Endpoint(string output, string active, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), WireGuardEndpointParser.Parse(output, [active]));

    [Theory]
    [InlineData("")]
    [InlineData("Home VPN\tpeer\t(none)\n")]
    [InlineData("Home VPN\tpeer\t[2001:db8::1]:51820\n")]
    [InlineData("Home VPN\tpeer\tvpn.example.com:51820\n")]
    [InlineData("Home VPN\tpeer\t203.0.113.1:0\n")]
    [InlineData("Home VPN peer 203.0.113.1:51820\n")]
    [InlineData("other\tpeer\t203.0.113.1:51820\n")]
    [InlineData("Home VPN\tpeer-a\t203.0.113.1:443\nHome VPN\tpeer-b\t198.51.100.1:443\n")]
    public void EndpointParserRejectsMissingAmbiguousOrUnsupportedEndpoints(string output) =>
        Assert.Null(WireGuardEndpointParser.Parse(output, ["Home VPN"]));

    private sealed class FakeRoutes : IWireGuardRouteTable
    {
        public List<(IPAddress Destination, WireGuardEndpointRoute Route)> Hosts { get; } = [];
        public List<string> Operations { get; } = [];
        private readonly Dictionary<int, int> _metrics = [];
        private HashSet<int>? _available;
        public int? FailIndex { get; set; }
        public string? FailOperation { get; set; }
        public WireGuardEndpointRoute? BestOverride { get; set; }
        public bool SuppressBest { get; set; }

        public void SetAvailable(params int[] indices) => _available = indices.ToHashSet();

        public void EnsureHost(IPAddress destination, int interfaceIndex, IPAddress gateway, int metric)
        {
            Fail("host", interfaceIndex);
            Operations.Add($"host:{interfaceIndex}:{metric}");
            Hosts.RemoveAll(x => x.Destination.Equals(destination) && x.Route.InterfaceIndex == interfaceIndex);
            Hosts.Add((destination, new(interfaceIndex, gateway, (uint)metric)));
        }

        public void SetMetric(int interfaceIndex, int metric)
        {
            Fail("metric", interfaceIndex);
            Operations.Add($"metric:{interfaceIndex}:{metric}");
            _metrics[interfaceIndex] = metric;
        }

        public WireGuardEndpointRoute? BestRoute(IPAddress destination) => SuppressBest ? null : BestOverride ?? Hosts
            .Where(x => x.Destination.Equals(destination) && (_available is null || _available.Contains(x.Route.InterfaceIndex)))
            .OrderBy(x => x.Route.Metric + _metrics.GetValueOrDefault(x.Route.InterfaceIndex, 25))
            .ThenBy(x => x.Route.InterfaceIndex)
            .Select(x => x.Route).FirstOrDefault();

        private void Fail(string operation, int index)
        {
            if (index == FailIndex && operation == FailOperation) throw new Win32Exception(1168);
        }
    }
}
