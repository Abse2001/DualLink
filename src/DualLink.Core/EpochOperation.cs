namespace DualLink.Core;

// A polled asynchronous operation belongs to one topology/route epoch. Removed
// work may finish later, but can never publish into its replacement's state.
public sealed class EpochOperation<T> : IDisposable
{
    private Task<T>? _task;
    private CancellationTokenSource? _cancellation;
    private string? _identity;

    // Completed work remains pending until the owner consumes its result.
    public bool IsPending => _task is not null;

    public void Start(string identity, Func<CancellationToken, Task<T>> operation, CancellationToken shutdown)
    {
        Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        try
        {
            _task = operation(cancellation.Token);
            _identity = identity;
            _cancellation = cancellation;
        }
        catch
        {
            cancellation.Cancel();
            cancellation.Dispose();
            throw;
        }
    }

    public bool TryComplete(string currentIdentity, out T? value)
    {
        value = default;
        if (_task is null) return false;
        if (_identity != currentIdentity) { Cancel(); return false; }
        if (!_task.IsCompleted) return false;
        if (_task.IsCompletedSuccessfully && _cancellation?.IsCancellationRequested != true)
            value = _task.Result;
        Cancel();
        return true;
    }

    public void Cancel()
    {
        var task = _task;
        var cancellation = _cancellation;
        _task = null;
        _cancellation = null;
        _identity = null;
        if (task is not null)
            _ = task.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { cancellation?.Cancel(); }
        finally { cancellation?.Dispose(); }
    }

    public void Dispose() => Cancel();
}
