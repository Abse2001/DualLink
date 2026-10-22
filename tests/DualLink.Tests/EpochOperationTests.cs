using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public class EpochOperationTests
{
    [Fact]
    public void MatchingSuccessIsPublishedOnce()
    {
        using var operation = new EpochOperation<int>();
        operation.Start("ethernet:gateway-one", _ => Task.FromResult(42), CancellationToken.None);

        Assert.True(operation.IsPending);
        Assert.True(operation.TryComplete("ethernet:gateway-one", out var value));
        Assert.Equal(42, value);
        Assert.False(operation.IsPending);
        Assert.False(operation.TryComplete("ethernet:gateway-one", out _));
    }

    [Fact]
    public void MatchingPendingOperationIsRetainedUntilItCompletes()
    {
        using var operation = new EpochOperation<bool>();
        var pending = Completion();
        operation.Start("ethernet:one", _ => pending.Task, CancellationToken.None);

        Assert.False(operation.TryComplete("ethernet:one", out var pendingValue));
        Assert.False(pendingValue);
        Assert.True(operation.IsPending);
        pending.SetResult(true);
        Assert.True(operation.TryComplete("ethernet:one", out var completedValue));
        Assert.True(completedValue);
    }

    [Fact]
    public void ObsoleteSuccessCannotVerifyAReplacementEpoch()
    {
        using var operation = new EpochOperation<bool>();
        var old = Completion();
        var current = Completion();
        operation.Start("ethernet:old-gateway", _ => old.Task, CancellationToken.None);
        operation.Start("ethernet:new-gateway", _ => current.Task, CancellationToken.None);
        old.SetResult(true);

        Assert.False(operation.TryComplete("ethernet:new-gateway", out var staleValue));
        Assert.False(staleValue);
        current.SetResult(true);
        Assert.True(operation.TryComplete("ethernet:new-gateway", out var currentValue));
        Assert.True(currentValue);
    }

    [Fact]
    public void ObsoleteFailureCannotReplaceTheCurrentResult()
    {
        using var operation = new EpochOperation<bool>();
        var old = Completion();
        var current = Completion();
        operation.Start("wifi:old", _ => old.Task, CancellationToken.None);
        operation.Start("ethernet:current", _ => current.Task, CancellationToken.None);
        old.SetException(new InvalidOperationException("Old topology failed after replacement"));
        current.SetResult(true);

        Assert.True(operation.TryComplete("ethernet:current", out var value));
        Assert.True(value);
    }

    [Fact]
    public void MismatchedCompletedResultIsDropped()
    {
        using var operation = new EpochOperation<bool>();
        operation.Start("ethernet:old", _ => Task.FromResult(true), CancellationToken.None);

        Assert.False(operation.TryComplete("ethernet:new", out var value));
        Assert.False(value);
        Assert.False(operation.IsPending);
    }

    [Fact]
    public void MismatchedPendingOperationIsCancelledImmediately()
    {
        using var operation = new EpochOperation<bool>();
        var cancellationReachedOperation = false;
        operation.Start("ethernet:old", token =>
        {
            token.Register(() => cancellationReachedOperation = true);
            return Completion().Task;
        }, CancellationToken.None);

        Assert.False(operation.TryComplete("ethernet:new", out _));
        Assert.True(cancellationReachedOperation);
        Assert.False(operation.IsPending);
    }

    [Fact]
    public void ExplicitReplacementCancelsOldWorkEvenWithTheSameIdentity()
    {
        using var operation = new EpochOperation<bool>();
        var cancellationReachedOperation = false;
        var old = Completion();
        operation.Start("ethernet:one", token =>
        {
            token.Register(() => cancellationReachedOperation = true);
            return old.Task;
        }, CancellationToken.None);
        operation.Start("ethernet:one", _ => Task.FromResult(false), CancellationToken.None);
        old.SetResult(true);

        Assert.True(cancellationReachedOperation);
        Assert.True(operation.TryComplete("ethernet:one", out var value));
        Assert.False(value);
    }

    [Fact]
    public void MatchingFaultCompletesWithTheDefaultValue()
    {
        using var operation = new EpochOperation<bool>();
        operation.Start("ethernet:one", _ => Task.FromException<bool>(new InvalidOperationException("Probe failed")), CancellationToken.None);

        Assert.True(operation.TryComplete("ethernet:one", out var value));
        Assert.False(value);
        Assert.False(operation.IsPending);
    }

    [Fact]
    public void MatchingCancellationCompletesWithTheDefaultValue()
    {
        using var operation = new EpochOperation<bool>();
        operation.Start("ethernet:one", _ => Task.FromCanceled<bool>(new CancellationToken(true)), CancellationToken.None);

        Assert.True(operation.TryComplete("ethernet:one", out var value));
        Assert.False(value);
        Assert.False(operation.IsPending);
    }

    [Fact]
    public void ShutdownCancelsOperationAndPreventsLateSuccess()
    {
        using var operation = new EpochOperation<bool>();
        using var shutdown = new CancellationTokenSource();
        var pending = Completion();
        CancellationToken observedToken = default;
        operation.Start("ethernet:one", token => { observedToken = token; return pending.Task; }, shutdown.Token);
        shutdown.Cancel();
        pending.SetResult(true);

        Assert.True(observedToken.IsCancellationRequested);
        Assert.True(operation.TryComplete("ethernet:one", out var value));
        Assert.False(value);
    }

    [Fact]
    public void SynchronousFactoryExceptionLeavesNoPendingWorkAndCancelsPreviousOperation()
    {
        using var operation = new EpochOperation<bool>();
        CancellationToken oldToken = default;
        operation.Start("old", token => { oldToken = token; return Completion().Task; }, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => operation.Start("new", _ =>
            throw new InvalidOperationException("Cannot create operation"), CancellationToken.None));

        Assert.True(oldToken.IsCancellationRequested);
        Assert.False(operation.IsPending);
        Assert.False(operation.TryComplete("new", out _));
        operation.Start("new", _ => Task.FromResult(true), CancellationToken.None);
        Assert.True(operation.TryComplete("new", out var value));
        Assert.True(value);
    }

    [Fact]
    public void RepeatedCancelAndDisposeAreSafeAndLateFailureIsDiscarded()
    {
        var operation = new EpochOperation<bool>();
        var old = Completion();
        operation.Start("ethernet:one", _ => old.Task, CancellationToken.None);
        operation.Cancel();
        operation.Cancel();
        operation.Dispose();
        old.SetException(new InvalidOperationException("Cancelled work finished late"));

        Assert.False(operation.IsPending);
        Assert.False(operation.TryComplete("ethernet:one", out _));
    }

    private static TaskCompletionSource<bool> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
