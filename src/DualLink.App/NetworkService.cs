using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DualLink.Core;

namespace DualLink.App;

public sealed class NetworkService
{
    private static readonly string[] ProbeTargets = ["1.1.1.1", "8.8.8.8"];

    public IReadOnlyDictionary<string, AdapterByteCounters> GetAdapterByteCounters()
    {
        var counters = new Dictionary<string, AdapterByteCounters>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(IsPhysicalInternetAdapter))
        {
            try
            {
                var statistics = adapter.GetIPv4Statistics();
                counters[adapter.Id] = new(statistics.BytesSent, statistics.BytesReceived);
            }
            catch (NetworkInformationException)
            {
                // A driver can disappear between enumeration and sampling.
            }
        }
        return counters;
    }

    public bool IsProtonTunnelActive() => NetworkInterface.GetAllNetworkInterfaces().Any(n =>
        n.OperationalStatus == OperationalStatus.Up &&
        ($"{n.Name} {n.Description}".Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
         $"{n.Name} {n.Description}".Contains("WireGuard", StringComparison.OrdinalIgnoreCase)));

    public IReadOnlyList<AdapterInfo> GetInternetAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .Where(IsPhysicalInternetAdapter)
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

    private static bool IsPhysicalInternetAdapter(NetworkInterface adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}";
        string[] excluded = ["Wintun", "WireGuard", "Proton", "Hyper-V", "VMware", "VirtualBox", "Loopback", "TAP-Windows", "DualLink Bond"];
        return !excluded.Any(value => identity.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ProbeResult> ProbeAsync(AdapterInfo adapter, string host, bool tunnelActive, CancellationToken token)
    {
        // A prepared WireGuard config uses /1 routes instead of the Windows /0 kill
        // switch. A single public ICMP target is pinned to each physical adapter so
        // upstream loss (including a cellular call) is detected without routing game
        // traffic outside the tunnel.
        var target = tunnelActive ? ProbeTargets[(adapter.InterfaceIndex & int.MaxValue) % ProbeTargets.Length] : host;
        if (string.IsNullOrWhiteSpace(target))
            return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0, "No IPv4 gateway was found");

        if (tunnelActive)
        {
            if (adapter.Gateway is null)
                return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0, "No IPv4 gateway was found");
            await EnsureHostRouteAsync(target, adapter, 5);
        }

        const int sampleCount = 2;
        async Task<(double? Latency, string? Error)> SampleAsync()
        {
            try
            {
                var result = await RunAsync("ping.exe", $"-4 -n 1 -w 350 -S {adapter.Address} {target}", token);
                var match = Regex.Match(result, @"time[=<](\d+)ms", RegexOptions.IgnoreCase);
                return match.Success ? (double.Parse(match.Groups[1].Value), null) : (null, "Probe timed out");
            }
            catch (Exception ex) { return (null, ex.Message); }
        }
        // Run samples concurrently: a dead upstream is detected in one timeout
        // window instead of waiting for two sequential 350 ms timeouts.
        var results = await Task.WhenAll(Enumerable.Range(0, sampleCount).Select(_ => SampleAsync()));
        var samples = results.Where(x => x.Latency.HasValue).Select(x => x.Latency!.Value).ToList();
        var failures = results.Count(x => !x.Latency.HasValue);
        var error = results.Select(x => x.Error).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var online = samples.Count > 0;
        var latency = online ? samples.Average() : 0;
        var jitter = samples.Count > 1 ? samples.Zip(samples.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
        var loss = failures / (double)sampleCount * 100;
        return new(adapter.Id, DateTimeOffset.Now, online, latency, jitter, loss, LinkScorer.Calculate(online, latency, jitter, loss), error);
    }

    public async Task<bool> VerifyBondedInternetAsync(CancellationToken token)
    {
        try
        {
            var result = await RunAsync("ping.exe", "-4 -n 1 -w 3000 1.1.1.1", token);
            return Regex.IsMatch(result, @"time[=<](\d+)ms", RegexOptions.IgnoreCase);
        }
        catch { return false; }
    }

    public async Task<double?> MeasureBondedInternetLatencyAsync(CancellationToken token)
    {
        try
        {
            var result = await RunAsync("ping.exe", "-4 -n 1 -w 1500 1.1.1.1", token);
            var match = Regex.Match(result, @"time[=<](\d+)ms", RegexOptions.IgnoreCase);
            return match.Success ? double.Parse(match.Groups[1].Value) : null;
        }
        catch { return null; }
    }

    public async Task ApplyMetricsAsync(IEnumerable<AdapterInfo> adapters, string preferredId, int preferredMetric, int backupMetric)
    {
        var commands = new List<string>();
        foreach (var adapter in adapters.Where(x => x.InterfaceIndex >= 0))
        {
            var metric = adapter.Id == preferredId ? preferredMetric : backupMetric;
            commands.Add($"Set-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {metric} -ErrorAction Stop");
        }
        if (commands.Count > 0) await RunPowerShellAsync(string.Join("; ", commands));
    }

    public async Task<bool> VerifyAdapterInternetAsync(AdapterInfo adapter, CancellationToken token)
    {
        if (adapter.Address is null || adapter.InterfaceIndex < 0) return false;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31,
                IPAddress.HostToNetworkOrder(adapter.InterfaceIndex));
            socket.Bind(new IPEndPoint(adapter.Address, 0));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(650));
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), deadline.Token);
            return socket.Connected;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task ApplyWireGuardEndpointRoutesAsync(IEnumerable<AdapterInfo> adapters, IPAddress endpoint, string? preferredId)
    {
        foreach (var adapter in adapters.Where(x => x.Gateway is not null))
        {
            var metric = adapter.Id == preferredId ? 1 : 50;
            await EnsureHostRouteAsync(endpoint.ToString(), adapter, metric);
        }
    }

    public async Task ApplyBondingEndpointRoutesAsync(IEnumerable<AdapterInfo> adapters, IPAddress endpoint)
    {
        foreach (var adapter in adapters.Where(x => x.Gateway is not null))
            await EnsureHostRouteAsync(endpoint.ToString(), adapter, 1);
    }

    public async Task ConfigureBondingTunnelAsync()
    {
        const string command =
            "$alias='DualLink Bond'; " +
            "Get-NetIPAddress -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue; " +
            "New-NetIPAddress -InterfaceAlias $alias -IPAddress '10.77.0.2' -PrefixLength 24 -AddressFamily IPv4 -ErrorAction Stop | Out-Null; " +
            "Set-NetIPInterface -InterfaceAlias $alias -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 5 -NlMtuBytes 1380; " +
            "Remove-NetRoute -DestinationPrefix @('0.0.0.0/1','128.0.0.0/1') -InterfaceAlias $alias -Confirm:$false -ErrorAction SilentlyContinue; " +
            "New-NetRoute -DestinationPrefix '0.0.0.0/1' -InterfaceAlias $alias -NextHop '10.77.0.1' -RouteMetric 1 -PolicyStore ActiveStore | Out-Null; " +
            "New-NetRoute -DestinationPrefix '128.0.0.0/1' -InterfaceAlias $alias -NextHop '10.77.0.1' -RouteMetric 1 -PolicyStore ActiveStore | Out-Null";
        await RunPowerShellAsync(command);
    }

    public async Task RemoveBondingRoutesAsync() => await RunPowerShellAsync(
        "$alias='DualLink Bond'; Remove-NetRoute -DestinationPrefix @('0.0.0.0/1','128.0.0.0/1') -InterfaceAlias $alias -Confirm:$false -ErrorAction SilentlyContinue");

    public async Task RemoveBondingEndpointRoutesAsync(IEnumerable<AdapterInfo> adapters, IPAddress endpoint)
    {
        foreach (var adapter in adapters)
            await RunPowerShellAsync($"Remove-NetRoute -DestinationPrefix '{endpoint}/32' -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue");
    }

    private static async Task EnsureHostRouteAsync(string destination, AdapterInfo adapter, int metric)
    {
        if (adapter.Gateway is null) return;
        var prefix = $"{destination}/32";
        var command = $"Remove-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue; " +
                      $"New-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {adapter.InterfaceIndex} -NextHop '{adapter.Gateway}' -RouteMetric {metric} -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Out-Null";
        await RunPowerShellAsync(command);
    }

    public async Task RestoreAutomaticMetricsAsync() =>
        await RunPowerShellAsync("Get-NetIPInterface -AddressFamily IPv4 | Where-Object {$_.InterfaceAlias -notmatch 'Loopback'} | Set-NetIPInterface -AutomaticMetric Enabled");

    public async Task RestoreManagedRoutesAsync(IEnumerable<AdapterInfo> adapters, IPAddress? endpoint)
    {
        var prefixes = ProbeTargets.Select(x => $"'{x}/32'").ToList();
        if (endpoint is not null) prefixes.Add($"'{endpoint}/32'");
        foreach (var adapter in adapters)
            await RunPowerShellAsync($"Remove-NetRoute -DestinationPrefix @({string.Join(',', prefixes)}) -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue");
        await RestoreAutomaticMetricsAsync();
    }

    private static Task<string> RunPowerShellAsync(string command) => RunAsync("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"", CancellationToken.None);

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

public sealed record AdapterByteCounters(long BytesSent, long BytesReceived);
