using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DualLink.Core;

namespace DualLink.App;

public sealed class NetworkService
{
    private static readonly HttpClient PublicIpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly string[] ProbeTargets = ["1.1.1.1", "8.8.8.8", "8.8.4.4", "9.9.9.9"];
    private const string VerificationTarget = "1.0.0.1";
    private readonly Dictionary<string, string> _probeTargetAssignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _observedPhysicalAdapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProbeRouteRepair = new(StringComparer.OrdinalIgnoreCase);
    private string? _probeRouteSignature;

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

    public async Task<string?> GetPublicIpAsync(CancellationToken token)
    {
        try
        {
            var value = (await PublicIpClient.GetStringAsync("https://checkip.amazonaws.com", token)).Trim();
            return IPAddress.TryParse(value, out var address) ? address.ToString() : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public IReadOnlyList<AdapterInfo> GetInternetAdapters()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ppp)
            .Where(IsPhysicalInternetAdapter)
            .ToArray();
        foreach (var adapter in candidates.Where(n => n.OperationalStatus == OperationalStatus.Up))
            _observedPhysicalAdapters.Add(adapter.Id);

        return candidates
        .Where(n => n.OperationalStatus == OperationalStatus.Up || _observedPhysicalAdapters.Contains(n.Id))
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
        .Where(x => x.InterfaceIndex >= 0)
        .ToList();
    }

    private static bool IsPhysicalInternetAdapter(NetworkInterface adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}";
        string[] excluded = ["Wintun", "WireGuard", "Proton", "Hyper-V", "VMware", "VirtualBox", "Loopback", "TAP-Windows", "DualLink Bond"];
        return !excluded.Any(value => identity.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyDictionary<string, string>> PrepareProbeRoutesAsync(
        IReadOnlyList<AdapterInfo> adapters, bool tunnelActive)
    {
        // Keep an adapter's target for the lifetime of the application. Re-indexing
        // the remaining adapters when a cable was removed made Wi-Fi inherit the
        // Ethernet target and left stale /32 routes that could survive reconnection.
        foreach (var adapter in adapters.Where(x => !_probeTargetAssignments.ContainsKey(x.Id)))
        {
            var used = _probeTargetAssignments.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _probeTargetAssignments[adapter.Id] = ProbeTargets.FirstOrDefault(x => !used.Contains(x))
                ?? ProbeTargets[_probeTargetAssignments.Count % ProbeTargets.Length];
        }
        var assignments = adapters.ToDictionary(
            x => x.Id, x => _probeTargetAssignments[x.Id], StringComparer.OrdinalIgnoreCase);

        // Status is part of the fingerprint because Windows may remove an active
        // host route on media disconnect while retaining the NIC's old address and
        // gateway. Without this, reconnecting Ethernet looked identical and its
        // physical bypass route was never recreated under WireGuard's /1 routes.
        var signature = $"{tunnelActive}|" + string.Join('|', adapters.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Id}:{x.Status}:{x.InterfaceIndex}:{x.Address}:{x.Gateway}:{assignments[x.Id]}"));
        if (signature == _probeRouteSignature) return assignments;

        // Route preparation is deliberately serialized. The old implementation
        // changed host routes inside concurrent probes, allowing two adapters to
        // race for the same destination and making healthy paths flicker offline.
        foreach (var adapter in adapters.Where(x => x.Gateway is not null))
            await EnsureHostRouteAsync(assignments[adapter.Id], adapter, 5);
        _probeRouteSignature = signature;
        return assignments;
    }

    public async Task<bool> RepairProbeRouteAsync(AdapterInfo adapter, string target)
    {
        if (adapter.Status != OperationalStatus.Up || adapter.Address is null || adapter.Gateway is null)
            return false;

        // An upstream-only outage does not change the NIC's Windows status,
        // address, interface index, or gateway. The normal route fingerprint then
        // remains unchanged even if Windows kept a stale adapter-bound host route.
        // Recreate that one route at a bounded cadence and immediately re-probe so
        // a preferred Ethernet path can rejoin while WireGuard remains active.
        var now = DateTimeOffset.UtcNow;
        if (_lastProbeRouteRepair.TryGetValue(adapter.Id, out var lastRepair) &&
            now - lastRepair < TimeSpan.FromSeconds(1))
            return false;

        _lastProbeRouteRepair[adapter.Id] = now;
        await EnsureHostRouteAsync(target, adapter, 5);
        _probeRouteSignature = null;
        return true;
    }

    public void MarkProbeRouteHealthy(string adapterId) =>
        _lastProbeRouteRepair.TryRemove(adapterId, out _);

    public async Task<ProbeResult> ProbeAsync(AdapterInfo adapter, string host, bool tunnelActive,
        CancellationToken token, string? assignedTarget = null, int timeoutMilliseconds = 225)
    {
        // A prepared WireGuard config uses /1 routes instead of the Windows /0 kill
        // switch. A single public ICMP target is pinned to each physical adapter so
        // upstream loss (including a cellular call) is detected without routing game
        // traffic outside the tunnel.
        var target = assignedTarget ?? host;
        if (adapter.Status != OperationalStatus.Up)
            // A driver-reported link loss is definitive. Do not hold the dead path
            // online for an extra stabilization cycle before failover.
            return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0,
                "Physical link disconnected");
        if (adapter.Address is null)
            return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0,
                "Physical link is up but no IPv4 address was assigned");
        if (string.IsNullOrWhiteSpace(target))
            return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0, "No IPv4 gateway was found");

        if (adapter.Gateway is null)
            return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0,
                "Physical link is up but no IPv4 gateway was found");

        const int sampleCount = 2;
        async Task<(double? Latency, string? Error)> SampleAsync()
        {
            try
            {
                // ICMP can be dropped by routers, mobile carriers, VPN routes, or
                // endpoint policy even while real Internet traffic works. Test an
                // actual TCP handshake through the exact physical interface.
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31,
                    IPAddress.HostToNetworkOrder(adapter.InterfaceIndex));
                socket.Bind(new IPEndPoint(adapter.Address!, 0));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMilliseconds, 75, 1000)));
                var stopwatch = Stopwatch.StartNew();
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(target), 443), deadline.Token);
                stopwatch.Stop();
                return socket.Connected ? (stopwatch.Elapsed.TotalMilliseconds, null) : (null, "TCP probe failed");
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return (null, ex is OperationCanceledException ? "Probe timed out" : ex.Message);
            }
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
        // Both attempts are interface-bound and run concurrently. If neither can
        // establish a real TCP connection, treat the upstream as dead immediately;
        // waiting for another monitor round leaves applications on a black-holed
        // path even though the Ethernet/Wi-Fi link itself still reports Up.
        if (!online)
            error = $"Internet unreachable: both interface-bound TCP probes failed ({error ?? "no response"})";
        return new(adapter.Id, DateTimeOffset.Now, online, latency, jitter, loss,
            LinkScorer.Calculate(online, latency, jitter, loss), error);
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

    public async Task<bool> VerifyRoutedInternetAsync(CancellationToken token)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(900));
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), deadline.Token);
            return socket.Connected;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<int?> GetPreferredRouteInterfaceAsync(IPAddress endpoint)
    {
        var command =
            $"$route = Get-NetRoute -DestinationPrefix '{endpoint}/32' -AddressFamily IPv4 -ErrorAction SilentlyContinue | " +
            "Sort-Object @{Expression={$_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric}} | Select-Object -First 1; " +
            "if ($route) { $route.InterfaceIndex }";
        var result = await RunPowerShellAsync(command);
        return int.TryParse(result.Trim(), out var index) ? index : null;
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

    public async Task<bool> VerifyAdapterInternetAsync(AdapterInfo adapter,
        IReadOnlyList<AdapterInfo> adapters, CancellationToken token)
    {
        if (adapter.Address is null || adapter.Gateway is null || adapter.InterfaceIndex < 0) return false;
        try
        {
            // Use a dedicated verification destination so per-adapter health-check
            // routes can never redirect this end-to-end test through another NIC.
            foreach (var candidate in adapters.Where(x => x.InterfaceIndex >= 0))
                await RunPowerShellAsync($"Remove-NetRoute -DestinationPrefix '{VerificationTarget}/32' -InterfaceIndex {candidate.InterfaceIndex} -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue");
            await EnsureHostRouteAsync(VerificationTarget, adapter, 1);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31,
                IPAddress.HostToNetworkOrder(adapter.InterfaceIndex));
            socket.Bind(new IPEndPoint(adapter.Address, 0));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(650));
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(VerificationTarget), 443), deadline.Token);
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

    public async Task<bool> MoveWireGuardEndpointRouteAsync(IReadOnlyList<AdapterInfo> adapters,
        IPAddress endpoint, AdapterInfo selected)
    {
        if (selected.Gateway is null || selected.InterfaceIndex < 0) return false;

        // Do the complete underlay switch in one PowerShell process. Starting a
        // process for every route used to leave WireGuard waiting behind several
        // hundred milliseconds of route maintenance after a cable disconnect.
        // Install the selected route before demoting backups so an endpoint route
        // exists throughout the switch and Windows emits an immediate route change
        // notification to WireGuard's connected UDP socket.
        var prefix = $"{endpoint}/32";
        var commands = new List<string>
        {
            $"Set-NetIPInterface -InterfaceIndex {selected.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 5 -ErrorAction SilentlyContinue",
            $"Remove-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {selected.InterfaceIndex} -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue",
            $"New-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {selected.InterfaceIndex} -NextHop '{selected.Gateway}' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null"
        };
        foreach (var backup in adapters.Where(x => x.Id != selected.Id && x.Gateway is not null && x.InterfaceIndex >= 0))
        {
            commands.Add($"Set-NetIPInterface -InterfaceIndex {backup.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 500 -ErrorAction SilentlyContinue");
            commands.Add($"Remove-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {backup.InterfaceIndex} -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue");
            commands.Add($"New-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {backup.InterfaceIndex} -NextHop '{backup.Gateway}' -RouteMetric 500 -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Out-Null");
        }
        await RunPowerShellAsync(string.Join("; ", commands));
        return await GetPreferredRouteInterfaceAsync(endpoint) == selected.InterfaceIndex;
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
                      $"New-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {adapter.InterfaceIndex} -NextHop '{adapter.Gateway}' -RouteMetric {metric} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null";
        await RunPowerShellAsync(command);
    }

    public async Task RestoreAutomaticMetricsAsync() =>
        await RunPowerShellAsync("Get-NetIPInterface -AddressFamily IPv4 | Where-Object {$_.InterfaceAlias -notmatch 'Loopback'} | Set-NetIPInterface -AutomaticMetric Enabled");

    public async Task RestoreManagedRoutesAsync(IEnumerable<AdapterInfo> adapters, IPAddress? endpoint)
    {
        var prefixes = ProbeTargets.Append(VerificationTarget).Select(x => $"'{x}/32'").ToList();
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
