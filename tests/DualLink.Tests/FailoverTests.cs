using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public class FailoverTests
{
    [Fact]
    public void HealthTrackerRejectsSingleTransientFailureInBalancedMode()
    {
        var tracker = new PathHealthTracker();
        var online = Probe("ethernet", true);
        Assert.True(tracker.Update(online, 2, 3, true).Online);
        Assert.True(tracker.Update(Probe("ethernet", false), 2, 3, true).Online);
        Assert.False(tracker.Update(Probe("ethernet", false), 2, 3, true).Online);
    }

    [Fact]
    public void HealthTrackerRequiresStableRecoveryButPhysicalLossIsImmediate()
    {
        var tracker = new PathHealthTracker();
        tracker.Update(Probe("ethernet", true), 2, 3, true);
        Assert.False(tracker.Update(Probe("ethernet", false), 2, 3, false).Online);
        Assert.False(tracker.Update(Probe("ethernet", true), 2, 3, true).Online);
        Assert.False(tracker.Update(Probe("ethernet", true), 2, 3, true).Online);
        Assert.True(tracker.Update(Probe("ethernet", true), 2, 3, true).Online);
    }

    private static ProbeResult Probe(string id, bool online) => new(id, DateTimeOffset.UtcNow,
        online, online ? 30 : 0, 0, online ? 0 : 100, online ? 90 : 0);

    [Fact]
    public void Default_monitor_cadence_is_fast_failover_profile() =>
        Assert.Equal(75, new DualLinkSettings().ProbeIntervalMilliseconds);

    [Fact] public void Offline_link_scores_zero() => Assert.Equal(0, LinkScorer.Calculate(false, 1, 0, 0));
    [Fact] public void Clean_low_latency_link_scores_high() => Assert.True(LinkScorer.Calculate(true, 20, 2, 0) > 90);

    [Fact]
    public void Failed_active_link_switches_immediately()
    {
        var controller = new FailoverController(new DualLinkSettings());
        controller.Evaluate([Probe("ethernet", true, 90), Probe("wifi", true, 70)]);
        var decision = controller.Evaluate([Probe("ethernet", false, 0), Probe("wifi", true, 70)]);
        Assert.True(decision.Changed);
        Assert.Equal("wifi", decision.ActiveAdapterId);
    }

    [Fact]
    public void Better_link_requires_confirmation_to_prevent_flapping()
    {
        var controller = new FailoverController(new DualLinkSettings { SwitchConfirmationCount = 3, MinimumScoreImprovement = 10 });
        controller.Evaluate([Probe("a", true, 60), Probe("b", true, 50)]);
        Assert.False(controller.Evaluate([Probe("a", true, 60), Probe("b", true, 90)]).Changed);
        Assert.False(controller.Evaluate([Probe("a", true, 60), Probe("b", true, 90)]).Changed);
        Assert.True(controller.Evaluate([Probe("a", true, 60), Probe("b", true, 90)]).Changed);
    }

    [Fact]
    public void Probe_stabilizer_rejects_single_loss_and_single_recovery()
    {
        var stabilizer = new ProbeStabilizer();
        Assert.True(stabilizer.Filter(Probe("ethernet", true, 90)).Online);
        Assert.True(stabilizer.Filter(Probe("ethernet", false, 0)).Online);
        Assert.False(stabilizer.Filter(Probe("ethernet", false, 0)).Online);
        Assert.False(stabilizer.Filter(Probe("ethernet", true, 90)).Online);
        Assert.True(stabilizer.Filter(Probe("ethernet", true, 90)).Online);
    }

    private static ProbeResult Probe(string id, bool online, double score) => new(id, DateTimeOffset.Now, online, 10, 1, 0, score);
}
