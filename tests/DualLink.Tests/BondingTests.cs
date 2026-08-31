using DualLink.Core;

namespace DualLink.Tests;

public sealed class BondingTests
{
    [Fact]
    public void SchedulerUsesBothLinksInProportionToDeliveryRate()
    {
        var scheduler = new AdaptiveBondingScheduler();
        var paths = new[]
        {
            new BondingPathSample("ethernet", true, 40, 1, 0, 20, 0, 1),
            new BondingPathSample("hotspot", true, 40, 1, 0, 30, 0, 1)
        };
        var selected = Enumerable.Range(0, 500)
            .Select(_ => scheduler.SelectPath(paths, 1_300, BondingMode.Bonding))
            .ToList();

        Assert.InRange(selected.Count(x => x == "ethernet"), 180, 220);
        Assert.InRange(selected.Count(x => x == "hotspot"), 280, 320);
    }

    [Fact]
    public void SchedulerImmediatelyExcludesDeadPath()
    {
        var scheduler = new AdaptiveBondingScheduler();
        var paths = new[]
        {
            new BondingPathSample("ethernet", false, 10, 0, 100, 20, 0, 0),
            new BondingPathSample("hotspot", true, 60, 5, 1, 10, 0, .9)
        };
        Assert.Equal("hotspot", scheduler.SelectPath(paths, 1_300, BondingMode.Bonding));
    }

    [Fact]
    public void ReorderBufferDeliversPacketsInSequence()
    {
        var now = DateTimeOffset.UtcNow;
        var buffer = new PacketReorderBuffer(TimeSpan.FromMilliseconds(50));

        Assert.Equal("one", Text(buffer.Add(1, "one"u8.ToArray(), now).Single()));
        Assert.Empty(buffer.Add(3, "three"u8.ToArray(), now));
        var output = buffer.Add(2, "two"u8.ToArray(), now);
        Assert.Equal(["two", "three"], output.Select(Text));
    }

    private static string Text(ReadOnlyMemory<byte> value) => System.Text.Encoding.UTF8.GetString(value.Span);
}
