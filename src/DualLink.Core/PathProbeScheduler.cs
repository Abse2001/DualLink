namespace DualLink.Core;

// One in-flight probe per adapter. A slow standby never blocks the next carrying
// path check, and an obsolete DHCP epoch cannot publish its eventual result.
public sealed class PathProbeScheduler : IDisposable
{
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public Task<ProbeResult> Start(string id, string identity, Func<CancellationToken, Task<ProbeResult>> probe,
        CancellationToken shutdown)
    {
        if (_jobs.TryGetValue(id, out var job)) return job.Task;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        try
        {
            var task = probe(cancellation.Token);
            _jobs[id] = new(identity, task, cancellation);
            return task;
        }
        catch { cancellation.Dispose(); throw; }
    }

    public IReadOnlyList<ProbeResult> Collect(IReadOnlyDictionary<string, string> identities)
    {
        var completed = new List<ProbeResult>();
        foreach (var item in _jobs.ToArray())
        {
            var job = item.Value;
            var valid = identities.TryGetValue(item.Key, out var identity) && identity == job.Identity;
            if (valid && !job.Task.IsCompleted) continue;
            _jobs.Remove(item.Key);
            if (valid && job.Task.IsCompletedSuccessfully) completed.Add(job.Task.Result);
            else if (valid && job.Task.IsFaulted)
                completed.Add(new(item.Key, DateTimeOffset.UtcNow, false, 0, 0, 100, 0, job.Task.Exception!.GetBaseException().Message));
            Release(job);
        }
        return completed;
    }

    private static void Release(Job job)
    {
        job.Cancellation.Cancel();
        job.Cancellation.Dispose();
        // Removed topology jobs may complete later. Observe faults without allowing
        // their result to contaminate the replacement interface's health.
        _ = job.Task.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose()
    {
        foreach (var job in _jobs.Values) Release(job);
        _jobs.Clear();
    }

    private sealed record Job(string Identity, Task<ProbeResult> Task, CancellationTokenSource Cancellation);
}
