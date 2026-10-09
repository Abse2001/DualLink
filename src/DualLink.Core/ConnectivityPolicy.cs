namespace DualLink.Core;

public static class ConnectivityPolicy
{
    // These HTTPS services belong to independent providers. A single failed
    // destination is not evidence that an adapter has no Internet.
    public static IReadOnlyList<string> PhysicalTargets { get; } = Array.AsReadOnly(new[] { "1.1.1.1", "8.8.8.8" });
    public static IReadOnlyList<string> TunnelTargets { get; } = Array.AsReadOnly(new[] { "1.0.0.1", "8.8.4.4" });

    public static int ProbeTimeout(int minimum, double? lastLatencyMs) =>
        (int)Math.Clamp(Math.Max(minimum, (lastLatencyMs ?? 0) * 4 + 100), 250, 1500);

    public static async Task<double?> FirstSuccessAsync(IEnumerable<Func<CancellationToken, Task<double?>>> attempts,
        CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = attempts.Select(attempt => attempt(stop.Token)).ToList();
        var all = pending.ToArray();
        double? success = null;
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                var result = await completed;
                if (result.HasValue) { success = result; break; }
            }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(all); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        return success;
    }

    public static ProbeResult Summarize(string id, DateTimeOffset now, IReadOnlyCollection<double?> results)
    {
        var good = results.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        if (good.Length == 0)
            return new(id, now, false, 0, 0, 100, 0, "No response from independent Internet probe destinations");
        var latency = good.Min();
        // Destination reachability is not packet loss. A provider filtering one
        // target must not impose a 50% loss penalty on an otherwise usable path.
        return new(id, now, true, latency, 0, 0, LinkScorer.Calculate(true, latency, 0, 0),
            good.Length < results.Count ? "Internet verified; one probe destination did not respond" : null);
    }
}
