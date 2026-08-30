using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using DualLink.Core;

namespace DualLink.App;

public sealed class NetworkService
{
    public bool IsProtonTunnelActive() => NetworkInterface.GetAllNetworkInterfaces().Any(n =>
        n.OperationalStatus == OperationalStatus.Up &&
        ($"{n.Name} {n.Description}".Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
         $"{n.Name} {n.Description}".Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
         $"{n.Name} {n.Description}".Contains("Wintun", StringComparison.OrdinalIgnoreCase)));

    public IReadOnlyList<AdapterInfo> GetInternetAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .Select(n =>
        {
            var props = n.GetIPProperties();
            var ipv4 = props.UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address));
            var gateway = props.GatewayAddresses
                .Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !x.Equals(IPAddress.Any));
            return new AdapterInfo(n.Id, n.Name, n.Description, n.NetworkInterfaceType, n.OperationalStatus,
                props.GetIPv4Properties()?.Index ?? -1, ipv4?.Address, gateway, null);
        })
        .Where(x => x.InterfaceIndex >= 0 && x.Address is not null)
        .ToList();

    public async Task<ProbeResult> ProbeAsync(AdapterInfo adapter, string host, bool tunnelActive, CancellationToken token)
    {
        // Proton's kill switch intentionally blocks packets that are bound directly to a
        // physical adapter. While the tunnel is active, probe only the adapter's local
        // gateway. This detects cable/router/hotspot loss without bypassing the VPN.
        var target = tunnelActive ? adapter.Gateway?.ToString() : host;
        if (string.IsNullOrWhiteSpace(target))
            return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0, "No IPv4 gateway was found");

        var samples = new List<double>();
        var failures = 0;
        string? error = null;
        for (var i = 0; i < 4; i++)
        {
            try
            {
                var result = await RunAsync("ping.exe", $"-4 -n 1 -w 1200 -S {adapter.Address} {target}", token);
                var match = Regex.Match(result, @"time[=<](\d+)ms", RegexOptions.IgnoreCase);
                if (match.Success) samples.Add(double.Parse(match.Groups[1].Value)); else failures++;
            }
            catch (Exception ex) { failures++; error = ex.Message; }
        }
        var online = samples.Count > 0;
        var latency = online ? samples.Average() : 0;
        var jitter = samples.Count > 1 ? samples.Zip(samples.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
        var loss = failures / 4d * 100;
        return new(adapter.Id, DateTimeOffset.Now, online, latency, jitter, loss, LinkScorer.Calculate(online, latency, jitter, loss), error);
    }

    public async Task ApplyMetricsAsync(IEnumerable<AdapterInfo> adapters, string preferredId, int preferredMetric, int backupMetric)
    {
        foreach (var adapter in adapters)
        {
            var metric = adapter.Id == preferredId ? preferredMetric : backupMetric;
            await RunPowerShellAsync($"Set-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {metric}");
        }
    }

    public async Task RestoreAutomaticMetricsAsync() =>
        await RunPowerShellAsync("Get-NetIPInterface -AddressFamily IPv4 | Where-Object {$_.InterfaceAlias -notmatch 'Loopback'} | Set-NetIPInterface -AutomaticMetric Enabled");

    private static Task<string> RunPowerShellAsync(string command) => RunAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"", CancellationToken.None);

    private static async Task<string> RunAsync(string file, string arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(token);
        var error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error.Trim());
        return output;
    }
}
