using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public class TunnelRecoveryPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RoamingFailuresDoNotRejectAPathBeforeItsDelayedHandshakeSucceeds()
    {
        var policy = new TunnelRecoveryPolicy();
        policy.RouteChanged("ethernet", "gateway-one", Start);

        for (var second = 0; second < 6; second++)
            Assert.False(policy.Observe("ethernet", "gateway-one", false, Start.AddSeconds(second)));

        Assert.False(policy.Observe("ethernet", "gateway-one", true, Start.AddSeconds(6)));
        Assert.False(policy.Observe("ethernet", "gateway-one", false, Start.AddSeconds(7)));
    }

    [Fact]
    public void OnlyFailuresAfterSettlingCountTowardRejectingTheTunnel()
    {
        var policy = new TunnelRecoveryPolicy(TimeSpan.FromSeconds(3));
        policy.RouteChanged("ethernet", "epoch-one", Start);
        Assert.False(policy.Observe("ethernet", "epoch-one", false, Start.AddSeconds(2)));
        Assert.False(policy.Observe("ethernet", "epoch-one", false, Start.AddSeconds(3)));
        Assert.True(policy.Observe("ethernet", "epoch-one", false, Start.AddSeconds(4)));
    }

    [Fact]
    public void SuccessfulVerificationEndsSettlingAndClearsRejection()
    {
        var policy = new TunnelRecoveryPolicy();
        policy.Reject("ethernet", Start);
        policy.RouteChanged("ethernet", "epoch-one", Start);

        Assert.False(policy.Observe("ethernet", "epoch-one", true, Start.AddSeconds(1)));
        Assert.True(policy.IsEligible("ethernet", Start.AddSeconds(1)));
        Assert.False(policy.Observe("ethernet", "epoch-one", false, Start.AddSeconds(2)));
        Assert.True(policy.Observe("ethernet", "epoch-one", false, Start.AddSeconds(3)));
    }

    [Fact]
    public void PhysicalFailureEscapesImmediatelyDuringSettling()
    {
        var policy = new TunnelRecoveryPolicy();
        var controller = new FailoverController(new());
        controller.Synchronize("ethernet");
        policy.RouteChanged("ethernet", "epoch-one", Start);
        policy.Reject("wifi", Start);

        var probes = new[] { Probe("ethernet", false), Probe("wifi", true) };
        var decision = controller.Evaluate(policy.EligibleProbes(probes, Start.AddSeconds(1)), "ethernet");

        Assert.Equal("wifi", decision.ActiveAdapterId);
        Assert.True(decision.Changed);
    }

    [Fact]
    public void RejectedEthernetRemainsAvailableWhenWifiIsDisabled()
    {
        var policy = new TunnelRecoveryPolicy();
        var controller = new FailoverController(new());
        controller.Synchronize("wifi");
        policy.Reject("ethernet", Start);
        var probes = new[] { Probe("ethernet", true), Probe("wifi", false) };

        var decision = controller.Evaluate(policy.EligibleProbes(probes, Start.AddSeconds(2)), "ethernet");

        Assert.Equal("ethernet", decision.ActiveAdapterId);
        Assert.True(decision.Changed);
        Assert.False(policy.IsEligible("ethernet", Start.AddSeconds(2)));
    }

    [Fact]
    public void HealthyWifiKeepsPreferredEthernetInCooldownUntilItExpires()
    {
        var policy = new TunnelRecoveryPolicy();
        var probes = new[] { Probe("ethernet", true), Probe("wifi", true) };
        policy.Reject("ethernet", Start);

        Assert.Equal("wifi", Assert.Single(policy.EligibleProbes(probes, Start.AddSeconds(14))).AdapterId);
        Assert.Equal(2, policy.EligibleProbes(probes, Start.AddSeconds(15)).Count);
    }

    [Fact]
    public void AllRejectedOnlinePathsAreRetriedButOfflinePathsAreNeverCandidates()
    {
        var policy = new TunnelRecoveryPolicy();
        policy.Reject("ethernet", Start);
        policy.Reject("wifi", Start);

        var eligible = policy.EligibleProbes(
            [Probe("ethernet", true), Probe("wifi", true), Probe("usb", false)], Start.AddSeconds(1));

        Assert.Equal(new[] { "ethernet", "wifi" }, eligible.Select(x => x.AdapterId));
        Assert.Empty(policy.EligibleProbes([Probe("ethernet", false)], Start.AddSeconds(1)));
    }

    [Fact]
    public void ANewAdapterDoesNotInheritAnotherAdaptersFailuresWithTheSameIdentity()
    {
        var policy = new TunnelRecoveryPolicy();
        Assert.False(policy.Observe("ethernet", "shared-identity", false, Start));
        Assert.False(policy.Observe("wifi", "shared-identity", false, Start));
        Assert.True(policy.Observe("wifi", "shared-identity", false, Start));
    }

    [Fact]
    public void AdapterIdCasingDoesNotResetFailureConfirmation()
    {
        var policy = new TunnelRecoveryPolicy();
        Assert.False(policy.Observe("ETHERNET", "epoch-one", false, Start));
        Assert.True(policy.Observe("ethernet", "epoch-one", false, Start));
    }

    [Fact]
    public void NewEpochGetsItsOwnSettlingWindowAndForgetClearsCooldown()
    {
        var policy = new TunnelRecoveryPolicy(TimeSpan.FromSeconds(2));
        policy.RouteChanged("ethernet", "old-gateway", Start);
        Assert.False(policy.Observe("ethernet", "old-gateway", false, Start.AddSeconds(2)));
        Assert.True(policy.Observe("ethernet", "old-gateway", false, Start.AddSeconds(3)));
        policy.Reject("ethernet", Start.AddSeconds(3));
        policy.Forget("ethernet");
        policy.RouteChanged("ethernet", "new-gateway", Start.AddSeconds(4));

        Assert.True(policy.IsEligible("ethernet", Start.AddSeconds(4)));
        Assert.False(policy.Observe("ethernet", "new-gateway", false, Start.AddSeconds(5)));
        Assert.False(policy.Observe("ethernet", "new-gateway", false, Start.AddSeconds(6)));
        Assert.True(policy.Observe("ethernet", "new-gateway", false, Start.AddSeconds(7)));
    }

    [Fact]
    public void RepairingTheSameEpochRestartsSettlingOnlyWhenExplicitlyReported()
    {
        var policy = new TunnelRecoveryPolicy(TimeSpan.FromSeconds(2));
        policy.RouteChanged("ethernet", "epoch-one", Start);
        Assert.False(policy.Observe("ethernet", "epoch-one", false, Start.AddSeconds(2)));
        policy.RouteChanged("ethernet", "epoch-one", Start.AddSeconds(3));

        Assert.False(policy.Observe("ethernet", "epoch-one", false, Start.AddSeconds(4)));
        Assert.False(policy.Observe("ethernet", "epoch-one", false, Start.AddSeconds(5)));
        Assert.True(policy.Observe("ethernet", "epoch-one", false, Start.AddSeconds(6)));
    }

    [Fact]
    public void SuccessResetsConsecutiveFailuresWithoutRequiringARouteChange()
    {
        var policy = new TunnelRecoveryPolicy();
        Assert.False(policy.Observe("ethernet", "epoch-one", false, Start));
        Assert.False(policy.Observe("ethernet", "epoch-one", true, Start));
        Assert.False(policy.Observe("ethernet", "epoch-one", false, Start));
        Assert.True(policy.Observe("ethernet", "epoch-one", false, Start));
    }

    [Fact]
    public void NegativeSettlingPeriodIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TunnelRecoveryPolicy(TimeSpan.FromSeconds(-1)));

    private static ProbeResult Probe(string id, bool online) =>
        new(id, Start, online, 25, 0, online ? 0 : 100, online ? 90 : 0);
}
