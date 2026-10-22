using System.ComponentModel;
using System.Net;
using DualLink.App;
using DualLink.Core;
using Xunit;

namespace DualLink.Tests;

public sealed class WireGuardPeerServiceTests
{
    private static readonly IPAddress Endpoint = IPAddress.Parse("203.0.113.7");
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
    private static readonly string OtherKey = Convert.ToBase64String(Enumerable.Range(2, 32).Select(x => (byte)x).ToArray());

    [Fact]
    public async Task RefreshUpdatesOnlyTheLivePeerEndpointAndPreservesSpacesInAlias()
    {
        const string alias = "Proton VPN Europe";
        var commands = new List<string[]>();
        var service = new WireGuardPeerService((arguments, _) =>
        {
            commands.Add(arguments.ToArray());
            return Task.FromResult(commands.Count == 1 ? $"{alias}\t{Key}\t{Endpoint}:51820\r\n" : "");
        }, () => [alias]);

        Assert.True(await service.RefreshEndpointAsync(Endpoint, CancellationToken.None));
        Assert.Collection(commands,
            query => Assert.Equal(["show", "all", "endpoints"], query),
            update => Assert.Equal(["set", alias, "peer", Key, "endpoint", "203.0.113.7:51820"], update));
    }

    [Theory]
    [InlineData("203.0.113.8:51820")]
    [InlineData("203.0.113.7:0")]
    [InlineData("203.0.113.7:65536")]
    [InlineData("203.0.113.7")]
    [InlineData("[2001:db8::7]:51820")]
    [InlineData("(none)")]
    [InlineData("not-an-endpoint")]
    public async Task MissingOrMismatchedEndpointNeverIssuesAnUpdate(string endpoint)
    {
        var commands = 0;
        var service = new WireGuardPeerService((_, _) =>
        {
            commands++;
            return Task.FromResult($"Proton\t{Key}\t{endpoint}\n");
        }, () => ["Proton"]);

        Assert.False(await service.RefreshEndpointAsync(Endpoint, CancellationToken.None));
        Assert.Equal(1, commands);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    public async Task MalformedPeerKeyNeverIssuesAnUpdate(string key)
    {
        var commands = 0;
        var service = new WireGuardPeerService((_, _) =>
        {
            commands++;
            return Task.FromResult($"Proton\t{key}\t{Endpoint}:51820\n");
        }, () => ["Proton"]);

        Assert.False(await service.RefreshEndpointAsync(Endpoint, CancellationToken.None));
        Assert.Equal(1, commands);
    }

    [Fact]
    public async Task MultipleMatchingLivePeersAreAmbiguousEvenWithTheSameRemoteAddress()
    {
        var commands = 0;
        var service = new WireGuardPeerService((_, _) =>
        {
            commands++;
            return Task.FromResult($"Proton\t{Key}\t{Endpoint}:51820\nOther\t{OtherKey}\t{Endpoint}:443\n");
        }, () => ["Proton", "Other"]);

        Assert.False(await service.RefreshEndpointAsync(Endpoint, CancellationToken.None));
        Assert.Equal(1, commands);
    }

    [Fact]
    public void InactiveTunnelCannotBeSelectedForRefresh()
    {
        Assert.Null(WireGuardPeerEndpoint.FindUnique($"Old Proton\t{Key}\t{Endpoint}:51820\n",
            ["New Proton"], Endpoint));
    }

    [Fact]
    public async Task TunnelDisappearingAfterObservationIsNotUpdated()
    {
        var enumerations = 0;
        var commands = 0;
        var service = new WireGuardPeerService((_, _) =>
        {
            commands++;
            return Task.FromResult($"Proton\t{Key}\t{Endpoint}:443\n");
        }, () => ++enumerations == 1 ? new[] { "Proton" } : Array.Empty<string>());

        Assert.False(await service.RefreshEndpointAsync(Endpoint, CancellationToken.None));
        Assert.Equal(1, commands);
    }

    [Fact]
    public async Task NoActiveTunnelDoesNotStartAProcess()
    {
        var service = new WireGuardPeerService((_, _) => throw new Xunit.Sdk.XunitException("Unexpected process"),
            () => Array.Empty<string>());
        Assert.False(await service.RefreshEndpointAsync(Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task MissingWireGuardExecutableIsARecoverableFailure()
    {
        var service = new WireGuardPeerService((_, _) => throw new FileNotFoundException(), () => ["Proton"]);
        Assert.False(await service.RefreshEndpointAsync(Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task FailedUpdateIsNotReportedAsSuccessful()
    {
        var service = new WireGuardPeerService((arguments, _) => arguments[0] == "show"
            ? Task.FromResult($"Proton\t{Key}\t{Endpoint}:443\n")
            : throw new Win32Exception(5), () => ["Proton"]);
        Assert.False(await service.RefreshEndpointAsync(Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task HungCommandTimesOutWithoutClaimingRecovery()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WireGuardPeerService(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); }
            return "";
        }, () => ["Proton"]);

        Assert.False(await service.RefreshEndpointAsync(Endpoint, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ShutdownCancellationPropagatesAndDoesNotIssueAnUpdate()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = 0;
        var service = new WireGuardPeerService(async (_, token) =>
        {
            commands++;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "";
        }, () => ["Proton"]);

        var refresh = service.RefreshEndpointAsync(Endpoint, cancel.Token);
        await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Equal(1, commands);
    }
}
