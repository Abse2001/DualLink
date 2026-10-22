using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using DualLink.Core;

namespace DualLink.App;

/// <summary>Refreshes a running peer's source-route cache without restarting its tunnel.</summary>
public sealed class WireGuardPeerService
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<string>> _runner;
    private readonly Func<IReadOnlyCollection<string>> _activeTunnelNames;

    public WireGuardPeerService(
        Func<IReadOnlyList<string>, CancellationToken, Task<string>>? runner = null,
        Func<IReadOnlyCollection<string>>? activeTunnelNames = null)
    {
        _runner = runner ?? RunAsync;
        _activeTunnelNames = activeTunnelNames ?? ActiveTunnelNames;
    }

    public async Task<bool> RefreshEndpointAsync(IPAddress expectedEndpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var names = _activeTunnelNames();
            if (names.Count == 0) return false;
            // Query only public peer metadata; never read a config or key dump.
            var output = await RunBoundedAsync(["show", "all", "endpoints"], token);
            var peer = WireGuardPeerEndpoint.FindUnique(output, names, expectedEndpoint);
            if (peer is null || !_activeTunnelNames().Contains(peer.TunnelName, StringComparer.OrdinalIgnoreCase))
                return false;

            // WireGuardNT's SetPeer passes a zeroed source address/interface to
            // SocketSetPeerEndpoint, invalidating its cached underlay even when
            // the remote endpoint is unchanged. This partial update preserves
            // keys, allowed IPs, listen port and adapter state.
            // https://git.zx2c4.com/wireguard-nt/tree/driver/ioctl.c
            // https://git.zx2c4.com/wireguard-nt/tree/driver/socket.c
            await RunBoundedAsync(["set", peer.TunnelName, "peer", peer.PublicKey,
                "endpoint", peer.Endpoint.ToString()], token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private async Task<string> RunBoundedAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        return await _runner(arguments, deadline.Token).WaitAsync(deadline.Token);
    }

    private static IReadOnlyCollection<string> ActiveTunnelNames() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
            ($"{n.Name} {n.Description}".Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
             $"{n.Name} {n.Description}".Contains("Proton", StringComparison.OrdinalIgnoreCase)))
        .Select(n => n.Name).ToArray();

    private static async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WireGuard", "wg.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("WireGuard command was not found.");
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            var output = await outputTask;
            await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException("WireGuard endpoint update failed.");
            return output;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            throw;
        }
    }
}
