namespace DualLink.Core;

public static class LinkScorer
{
    public static double Calculate(bool online, double latencyMs, double jitterMs, double lossPercent)
    {
        if (!online) return 0;
        var latencyPenalty = Math.Min(45, latencyMs * 0.22);
        var jitterPenalty = Math.Min(20, jitterMs * 0.5);
        var lossPenalty = Math.Min(35, lossPercent * 7);
        return Math.Round(Math.Max(0, 100 - latencyPenalty - jitterPenalty - lossPenalty), 1);
    }
}
