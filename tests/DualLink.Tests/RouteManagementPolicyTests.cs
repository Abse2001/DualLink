using System.Net;
using System.Net.NetworkInformation;
using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public sealed class RouteManagementPolicyTests
{
    [Theory]
    [InlineData(false, false, false, RouteManagementMode.Physical)]
    [InlineData(false, false, true, RouteManagementMode.Physical)]
    [InlineData(true, false, false, RouteManagementMode.WaitingForWireGuardEndpoint)]
    [InlineData(true, false, true, RouteManagementMode.WireGuard)]
    [InlineData(false, true, false, RouteManagementMode.RelayBonding)]
    [InlineData(false, true, true, RouteManagementMode.RelayBonding)]
    [InlineData(true, true, false, RouteManagementMode.ConflictingTunnels)]
    [InlineData(true, true, true, RouteManagementMode.ConflictingTunnels)]
    public void DetectedRouteOwnerAndEndpointAvailabilityDetermineMode(bool wireGuardDetected,
        bool bondingActive, bool endpointKnown, RouteManagementMode expected)
    {
        Assert.Equal(expected, RouteManagementPolicy.Resolve(wireGuardDetected, bondingActive, endpointKnown));
    }

    [Fact]
    public void LegacyUncheckedProtonPreferenceCannotSendActiveWireGuardIntoPhysicalRouteProof()
    {
        var scenario = new RouteScenario { WireGuardDetected = true, LegacyProtonPreference = false };

        // Reproduce the screenshot: ordinary Internet stays in the VPN as it
        // should, so demanding a physical best route necessarily rejects it.
        Assert.False(scenario.Physical.Apply(scenario.Adapters, "ethernet", 10, 60));
        Assert.Equal(RouteScenario.TunnelIndex, scenario.OrdinaryWinner());
        scenario.ResetMutationCount();

        Assert.False(scenario.LegacyProtonPreference);
        var mode = RouteManagementPolicy.Resolve(scenario.WireGuardDetected, false, true);
        Assert.Equal(RouteManagementMode.WireGuard, mode);
        Assert.True(scenario.Apply(mode, scenario.Adapters[0]));
        Assert.Equal(0, scenario.PhysicalMutations);
        Assert.Equal(scenario.Adapters[0].InterfaceIndex, scenario.Table.BestRoute(RouteScenario.Endpoint)!.InterfaceIndex);
        Assert.Equal(RouteScenario.TunnelIndex, scenario.OrdinaryWinner());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothLegacyPreferenceValuesUseOrdinarySwitchingWhenWireGuardIsAbsent(bool legacyPreference)
    {
        var scenario = new RouteScenario { LegacyProtonPreference = legacyPreference };
        var mode = RouteManagementPolicy.Resolve(scenario.WireGuardDetected, false, true);

        Assert.Equal(RouteManagementMode.Physical, mode);
        Assert.True(scenario.Apply(mode, scenario.Adapters[1]));
        Assert.Equal(scenario.Adapters[1].InterfaceIndex, scenario.OrdinaryWinner());
        Assert.True(scenario.PhysicalMutations > 0);
        Assert.Empty(scenario.Table.Hosts);
    }

    [Fact]
    public void UnknownActiveWireGuardEndpointWaitsWithoutMutatingPhysicalRoutes()
    {
        var scenario = new RouteScenario { WireGuardDetected = true };
        var mode = RouteManagementPolicy.Resolve(true, false, false);

        Assert.Equal(RouteManagementMode.WaitingForWireGuardEndpoint, mode);
        Assert.Null(scenario.Apply(mode, scenario.Adapters[0]));
        Assert.Equal(0, scenario.PhysicalMutations);
        Assert.Empty(scenario.Table.Hosts);
        Assert.Equal(RouteScenario.TunnelIndex, scenario.OrdinaryWinner());
    }

    [Fact]
    public void EndpointDiscoveryEnablesOnlyTheWireGuardRouteCoordinator()
    {
        var scenario = new RouteScenario { WireGuardDetected = true };
        var waiting = RouteManagementPolicy.Resolve(true, false, false);
        Assert.Null(scenario.Apply(waiting, scenario.Adapters[0]));

        var discovered = RouteManagementPolicy.Resolve(true, false, true);
        Assert.True(scenario.Apply(discovered, scenario.Adapters[0]));
        Assert.Equal(0, scenario.PhysicalMutations);
        Assert.Equal(scenario.Adapters[0].InterfaceIndex, scenario.Table.BestRoute(RouteScenario.Endpoint)!.InterfaceIndex);
    }

    [Fact]
    public void RepeatedWireGuardEnableDisableAndAdapterSwitchesSelectTheCorrectRouteOwner()
    {
        var scenario = new RouteScenario { LegacyProtonPreference = false };
        for (var cycle = 0; cycle < 100; cycle++)
        {
            foreach (var selected in scenario.Adapters)
            {
                scenario.WireGuardDetected = false;
                Assert.True(scenario.Apply(RouteManagementPolicy.Resolve(false, false, true), selected));
                Assert.Equal(selected.InterfaceIndex, scenario.OrdinaryWinner());

                scenario.WireGuardDetected = true;
                scenario.ResetMutationCount();
                Assert.Null(scenario.Apply(RouteManagementPolicy.Resolve(true, false, false), selected));
                Assert.Equal(0, scenario.PhysicalMutations);
                Assert.True(scenario.Apply(RouteManagementPolicy.Resolve(true, false, true), selected));
                Assert.Equal(0, scenario.PhysicalMutations);
                Assert.Equal(selected.InterfaceIndex, scenario.Table.BestRoute(RouteScenario.Endpoint)!.InterfaceIndex);
                Assert.Equal(RouteScenario.TunnelIndex, scenario.OrdinaryWinner());
            }
        }
    }

    [Fact]
    public void SimultaneousRelayAndWireGuardSuppressAutomaticRouteMutations()
    {
        var scenario = new RouteScenario { WireGuardDetected = true };
        var mode = RouteManagementPolicy.Resolve(true, true, true);

        Assert.Equal(RouteManagementMode.ConflictingTunnels, mode);
        Assert.Null(scenario.Apply(mode, scenario.Adapters[0]));
        Assert.Equal(0, scenario.PhysicalMutations);
        Assert.Empty(scenario.Table.Hosts);
        Assert.Equal(RouteScenario.TunnelIndex, scenario.OrdinaryWinner());
    }

    private sealed class RouteScenario
    {
        public const int TunnelIndex = 99;
        public static readonly IPAddress Endpoint = IPAddress.Parse("203.0.113.10");
        private readonly Dictionary<int, int> _metrics = new() { [10] = 10, [20] = 60 };
        public bool WireGuardDetected { get; set; }
        public bool LegacyProtonPreference { get; set; }
        public int PhysicalMutations { get; private set; }
        public FakeEndpointRoutes Table { get; } = new();
        public PhysicalRouteCoordinator Physical { get; }
        private readonly WireGuardRouteCoordinator _wireGuard;
        public IReadOnlyList<AdapterInfo> Adapters { get; } = new[]
        {
            Adapter("ethernet", 10, "192.0.2.10", "192.0.2.1"),
            Adapter("wifi", 20, "198.51.100.20", "198.51.100.1")
        };

        public RouteScenario()
        {
            Physical = new((index, metric) => { PhysicalMutations++; _metrics[index] = metric; },
                _ => 0u, OrdinaryWinner);
            _wireGuard = new(Table);
        }

        public int? OrdinaryWinner() => WireGuardDetected ? TunnelIndex : _metrics.OrderBy(x => x.Value).First().Key;
        public void ResetMutationCount() => PhysicalMutations = 0;

        public bool? Apply(RouteManagementMode mode, AdapterInfo selected) => mode switch
        {
            RouteManagementMode.Physical => Physical.Apply(Adapters, selected.Id, 10, 60),
            RouteManagementMode.WireGuard => _wireGuard.Reconcile(Adapters, Endpoint, selected),
            _ => null
        };

        private static AdapterInfo Adapter(string id, int index, string address, string gateway) => new(
            id, id, id, NetworkInterfaceType.Ethernet, OperationalStatus.Up, index,
            IPAddress.Parse(address), IPAddress.Parse(gateway), null);
    }

    private sealed class FakeEndpointRoutes : IWireGuardRouteTable
    {
        public Dictionary<int, WireGuardEndpointRoute> Hosts { get; } = new();
        private readonly Dictionary<int, int> _metrics = new();
        public void EnsureHost(IPAddress destination, int interfaceIndex, IPAddress gateway, int metric)
        {
            Assert.Equal(RouteScenario.Endpoint, destination);
            Hosts[interfaceIndex] = new(interfaceIndex, gateway, (uint)metric);
        }
        public void SetMetric(int interfaceIndex, int metric) => _metrics[interfaceIndex] = metric;
        public WireGuardEndpointRoute? BestRoute(IPAddress destination) => Hosts.Values
            .OrderBy(route => (long)route.Metric + _metrics.GetValueOrDefault(route.InterfaceIndex)).FirstOrDefault();
    }
}
