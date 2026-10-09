using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.IO;
using DualLink.Core;

namespace DualLink.App;

public sealed class NetworkService
{
    private static readonly HttpClient PublicIpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    // Physical-path probes must never share a destination with the routed tunnel
    // verification. A /32 probe route intentionally bypasses WireGuard; reusing
    // that address for tunnel verification made a dead Ethernet route look like a
    // failed WireGuard handoff and caused the endpoint route to be rebuilt forever.
    private static readonly string[] LegacyProbeTargets = ["8.8.4.4", "9.9.9.9", "149.112.112.112", "1.0.0.1"];
    private readonly HashSet<string> _observedPhysicalAdapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _latencies = new(StringComparer.OrdinalIgnoreCase);
    private string? _probeRouteSignature;
    private DateTimeOffset _lastProbeRouteAudit;
    private readonly HashSet<int> _cleanedInterfaces = [];

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

    public async Task<IPAddress?> GetActiveWireGuardEndpointAsync(CancellationToken token)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wg.exe");
        if (!File.Exists(executable)) return null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            // Query only endpoints; never request a config dump or private keys.
            var output = await RunAsync(executable, "show all endpoints", deadline.Token);
            var names = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up)
                .Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var endpoints = output.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(fields => fields.Length == 3 && names.Contains(fields[0]))
                .Select(fields => fields[2].Split(':')[0]).Select(value => IPAddress.TryParse(value, out var ip) ? ip : null)
                .Where(ip => ip?.AddressFamily == AddressFamily.InterNetwork).Distinct().ToArray();
            return endpoints.Length == 1 ? endpoints[0] : null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or OperationCanceledException) { return null; }
    }

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
            var ipv4 = props.UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(x.Address) && !x.Address.ToString().StartsWith("169.254.") &&
                x.DuplicateAddressDetectionState is not (DuplicateAddressDetectionState.Tentative or DuplicateAddressDetectionState.Duplicate));
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

    public Task<IReadOnlyDictionary<string, string>> PrepareProbeRoutesAsync(
        IReadOnlyList<AdapterInfo> adapters, bool tunnelActive)
    {
        var assignments = adapters.ToDictionary(x => x.Id, _ => ConnectivityPolicy.PhysicalTargets[0], StringComparer.OrdinalIgnoreCase);
        var signature = $"{tunnelActive}|" + string.Join('|', adapters.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Id}:{x.Status}:{x.InterfaceIndex}:{x.Address}:{x.Gateway}"));
        if (signature == _probeRouteSignature && DateTimeOffset.UtcNow - _lastProbeRouteAudit < TimeSpan.FromSeconds(1))
            return Task.FromResult<IReadOnlyDictionary<string, string>>(assignments);

        var success = true;
        foreach (var adapter in adapters.Where(IsUsable))
        {
            try
            {
                if (!_cleanedInterfaces.Contains(adapter.InterfaceIndex))
                {
                    WindowsRouteTable.RemoveHosts(LegacyProbeTargets.Select(IPAddress.Parse), [adapter.InterfaceIndex]);
                    _cleanedInterfaces.Add(adapter.InterfaceIndex);
                }
                // Both targets exist on every interface. IP_UNICAST_IF and Bind
                // select the exact NIC; no assignment reuse or four-NIC limit.
                foreach (var target in ConnectivityPolicy.PhysicalTargets)
                    WindowsRouteTable.EnsureHost(IPAddress.Parse(target), adapter.InterfaceIndex, adapter.Gateway!, 5);
            }
            catch (Win32Exception error)
            {
                success = false;
                AppLog.Write($"Probe route preparation for {adapter.Name}: {error.Message}");
            }
        }
        _probeRouteSignature = success ? signature : null;
        _lastProbeRouteAudit = DateTimeOffset.UtcNow;
        return Task.FromResult<IReadOnlyDictionary<string, string>>(assignments);
    }

    public static bool IsUsable(AdapterInfo adapter) => adapter.Status == OperationalStatus.Up &&
        adapter.Address is not null && adapter.Gateway is not null && adapter.InterfaceIndex > 0;

    public async Task<ProbeResult> ProbeAsync(AdapterInfo adapter, string host, bool tunnelActive,
        CancellationToken token, string? assignedTarget = null, int timeoutMilliseconds = 225)
    {
        // A prepared WireGuard config uses /1 routes instead of the Windows /0
        // firewall policy. Two HTTPS targets are routed over every physical NIC;
        // game traffic remains inside the live tunnel.
        if (adapter.Status != OperationalStatus.Up)
            // A driver-reported link loss is definitive. Do not hold the dead path
            // online for an extra stabilization cycle before failover.
            return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0,
                "Physical link disconnected");
        if (adapter.Address is null)
            return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0,
                "Physical link is up but no IPv4 address was assigned");
        if (adapter.Gateway is null)
            return new(adapter.Id, DateTimeOffset.Now, false, 0, 0, 100, 0,
                "Physical link is up but no IPv4 gateway was found");

        var timeout = ConnectivityPolicy.ProbeTimeout(timeoutMilliseconds,
            _latencies.TryGetValue(adapter.Id, out var latency) ? latency : null);
        async Task<double?> SampleAsync(string target, CancellationToken attemptToken)
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
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(timeout));
                var stopwatch = Stopwatch.StartNew();
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(target), 443), deadline.Token);
                stopwatch.Stop();
                return socket.Connected ? stopwatch.Elapsed.TotalMilliseconds : null;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                return null;
            }
        }
        // Run samples concurrently: a dead upstream is detected in one timeout
        // window instead of waiting for two sequential 350 ms timeouts.
        var result = await ConnectivityPolicy.FirstSuccessAsync(ConnectivityPolicy.PhysicalTargets.Select(target =>
            new Func<CancellationToken, Task<double?>>(attemptToken => SampleAsync(target, attemptToken))), token);
        var probe = ConnectivityPolicy.Summarize(adapter.Id, DateTimeOffset.Now, new double?[] { result });
        if (probe.Online)
        {
            var prior = _latencies.GetValueOrDefault(adapter.Id, probe.LatencyMs);
            var smoothed = prior * .8 + probe.LatencyMs * .2;
            var jitter = Math.Abs(probe.LatencyMs - prior);
            _latencies[adapter.Id] = smoothed;
            probe = probe with { LatencyMs = smoothed, JitterMs = jitter, Score = LinkScorer.Calculate(true, smoothed, jitter, 0) };
        }
        return probe;
    }

    public Task<bool> VerifyBondedInternetAsync(CancellationToken token) => VerifyTunnelInternetAsync("DualLink Bond", token);

    public Task<bool> VerifyRoutedInternetAsync(CancellationToken token) => VerifyTunnelInternetAsync(null, token);

    private async Task<bool> VerifyTunnelInternetAsync(string? alias, CancellationToken token)
    {
        // Bind verification to the tunnel itself. A successful direct physical
        // connection can never be mistaken for a successful VPN handoff.
        var tunnel = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
            (alias is not null ? n.Name == alias : $"{n.Name} {n.Description}".Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                $"{n.Name} {n.Description}".Contains("Proton", StringComparison.OrdinalIgnoreCase)));
        if (tunnel is null) return false;
        var properties = tunnel.GetIPProperties();
        var address = properties.UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
        var index = properties.GetIPv4Properties()?.Index;
        if (address is null || index is null) return false;
        var results = await Task.WhenAll(ConnectivityPolicy.TunnelTargets.Select(async target =>
            await ConnectAsync(address, index.Value, target, 800, token)));
        return results.Any(x => x.HasValue);
    }

    private static async Task<double?> ConnectAsync(IPAddress source, int index, string target, int timeout, CancellationToken token)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(index));
            socket.Bind(new IPEndPoint(source, 0));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            var clock = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(target), 443), deadline.Token);
            return socket.Connected ? clock.Elapsed.TotalMilliseconds : null;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    public Task<int?> GetPreferredRouteInterfaceAsync(IPAddress endpoint) => Task.FromResult(WindowsRouteTable.BestInterface(endpoint));

    public async Task<double?> MeasureBondedInternetLatencyAsync(CancellationToken token)
    {
        try
        {
            var result = await RunAsync("ping.exe", "-4 -n 1 -w 500 208.67.222.222", token);
            var match = Regex.Match(result, @"time[=<](\d+)ms", RegexOptions.IgnoreCase);
            return match.Success ? double.Parse(match.Groups[1].Value) : null;
        }
        catch { return null; }
    }

    public Task ApplyMetricsAsync(IEnumerable<AdapterInfo> adapters, string preferredId, int preferredMetric, int backupMetric)
    {
        // Promote the selected default path before demoting others. WireGuard's
        // underlay/MTU selection also observes default routes and interface metrics.
        foreach (var adapter in adapters.Where(IsUsable).OrderByDescending(x => x.Id == preferredId))
        {
            var metric = adapter.Id == preferredId ? preferredMetric : backupMetric;
            WindowsRouteTable.SetMetric(adapter.InterfaceIndex, metric);
        }
        return Task.CompletedTask;
    }

    public async Task<bool> VerifyAdapterInternetAsync(AdapterInfo adapter,
        IReadOnlyList<AdapterInfo> adapters, CancellationToken token)
    {
        return (await ProbeAsync(adapter, "", false, token, timeoutMilliseconds: 350)).Online;
    }

    public async Task ApplyWireGuardEndpointRoutesAsync(IEnumerable<AdapterInfo> adapters, IPAddress endpoint, string? preferredId)
    {
        foreach (var adapter in adapters.Where(IsUsable))
        {
            var metric = adapter.Id == preferredId ? 1 : 5000;
            await EnsureHostRouteAsync(endpoint.ToString(), adapter, metric);
        }
    }

    public async Task<bool> MoveWireGuardEndpointRouteAsync(IReadOnlyList<AdapterInfo> adapters,
        IPAddress endpoint, AdapterInfo selected)
    {
        if (!IsUsable(selected)) return false;

        // Native updates preserve existing rows and avoid process launch latency.
        // Keep both the exact endpoint route and default-interface preference in
        // agreement, then ask Windows for the actual winning route.
        WindowsRouteTable.EnsureHost(endpoint, selected.InterfaceIndex, selected.Gateway!, 1);
        WindowsRouteTable.SetMetric(selected.InterfaceIndex, 5);
        foreach (var backup in adapters.Where(x => x.Id != selected.Id && IsUsable(x)))
        {
            try
            {
                WindowsRouteTable.EnsureHost(endpoint, backup.InterfaceIndex, backup.Gateway!, 5000);
                WindowsRouteTable.SetMetric(backup.InterfaceIndex, 500);
            }
            catch (Win32Exception error) { AppLog.Write($"Standby endpoint route for {backup.Name}: {error.Message}"); }
        }
        return await GetPreferredRouteInterfaceAsync(endpoint) == selected.InterfaceIndex;
    }

    public async Task ApplyBondingEndpointRoutesAsync(IEnumerable<AdapterInfo> adapters, IPAddress endpoint)
    {
        foreach (var adapter in adapters.Where(IsUsable))
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

    private static Task EnsureHostRouteAsync(string destination, AdapterInfo adapter, int metric)
    {
        if (IsUsable(adapter)) WindowsRouteTable.EnsureHost(IPAddress.Parse(destination), adapter.InterfaceIndex, adapter.Gateway!, metric);
        return Task.CompletedTask;
    }

    public async Task RestoreAutomaticMetricsAsync() =>
        await RunPowerShellAsync("Get-NetIPInterface -AddressFamily IPv4 | Where-Object {$_.InterfaceAlias -notmatch 'Loopback'} | Set-NetIPInterface -AutomaticMetric Enabled");

    public async Task RestoreManagedRoutesAsync(IEnumerable<AdapterInfo> adapters, IPAddress? endpoint)
    {
        var physical = adapters.ToArray();
        var destinations = ConnectivityPolicy.PhysicalTargets.Concat(LegacyProbeTargets).Select(IPAddress.Parse).ToList();
        if (endpoint is not null) destinations.Add(endpoint);
        WindowsRouteTable.RemoveHosts(destinations, physical.Select(x => x.InterfaceIndex));
        foreach (var adapter in physical.Where(IsUsable)) WindowsRouteTable.SetMetric(adapter.InterfaceIndex, 0, automatic: true);
        _probeRouteSignature = null;
        _cleanedInterfaces.Clear();
        await Task.CompletedTask;
    }

    private static Task<string> RunPowerShellAsync(string command) => RunAsync("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"", CancellationToken.None);

    private static async Task<string> RunAsync(string file, string arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
            ? $"{Path.GetFileName(file)} exited with code {process.ExitCode}." : error.Trim());
        return output;
    }
}

public sealed record AdapterByteCounters(long BytesSent, long BytesReceived);
