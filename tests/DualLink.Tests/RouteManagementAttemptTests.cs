using System.ComponentModel;
using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public sealed class RouteManagementAttemptTests
{
    [Fact]
    public async Task SuccessfulRouteMutationHasNoErrorStatus()
    {
        var applied = false;
        var error = await RouteManagementAttempt.RunAsync(() => { applied = true; return Task.CompletedTask; });

        Assert.True(applied);
        Assert.Null(error);
    }

    [Fact]
    public async Task FailedRouteProofDoesNotAbortTheRestOfTheMonitorRound()
    {
        var rows = new List<string>();
        var error = await RouteManagementAttempt.RunAsync(() =>
            throw new InvalidOperationException("Windows could not apply the selected Internet adapter's route preference."));
        rows.AddRange(["Ethernet: online", "Wi-Fi: online"]);

        Assert.Equal("Windows could not apply the selected Internet adapter's route preference.", error);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task AsynchronousNativeFailureReturnsAnErrorAndCanRetryNextRound()
    {
        var first = await RouteManagementAttempt.RunAsync(() => Task.FromException(new Win32Exception(1168)));
        var second = await RouteManagementAttempt.RunAsync(() => Task.CompletedTask);

        Assert.NotEmpty(first!);
        Assert.Null(second);
    }

    [Fact]
    public async Task AsynchronousRouteProofFailureReturnsItsStatus()
    {
        Assert.Equal("Adapter changed while routing", await RouteManagementAttempt.RunAsync(() =>
            Task.FromException(new InvalidOperationException("Adapter changed while routing"))));
    }

    [Fact]
    public async Task NativeFailureBeforeCreatingATaskAlsoReturnsItsStatus()
    {
        var failure = new Win32Exception(1168);
        Assert.Equal(failure.Message, await RouteManagementAttempt.RunAsync(() => throw failure));
    }

    [Fact]
    public async Task ShutdownCancellationPropagatesInsteadOfBecomingARouteError()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RouteManagementAttempt.RunAsync(() =>
            Task.FromCanceled(cancellation.Token)));
    }

    [Fact]
    public async Task UnexpectedProgrammingFailuresAreNotHiddenAsTransientRouteStatus()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => RouteManagementAttempt.RunAsync(() =>
            throw new ArgumentException("Invalid route specification")));
    }
}
