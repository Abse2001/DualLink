using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace DualLink.App;

public sealed class WireGuardConfigService
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink");
    private static readonly string EndpointStatePath = Path.Combine(StateDirectory, "wireguard-endpoint.txt");

    public async Task<PreparedWireGuardConfig> PrepareAsync(string sourcePath)
    {
        var content = await File.ReadAllTextAsync(sourcePath);
        var endpointValue = Regex.Match(content, @"(?mi)^\s*Endpoint\s*=\s*(.+?)\s*$").Groups[1].Value;
        if (string.IsNullOrWhiteSpace(endpointValue))
            throw new InvalidOperationException("The selected file has no WireGuard Endpoint entry.");

        var host = ExtractHost(endpointValue);
        var endpoint = IPAddress.TryParse(host, out var parsed)
            ? parsed
            : (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (endpoint is null)
            throw new InvalidOperationException("The Proton WireGuard endpoint could not be resolved to IPv4.");

        var port = ExtractPort(endpointValue);
        if (port is null)
            throw new InvalidOperationException("The Proton WireGuard endpoint has no valid UDP port.");

        var prepared = Regex.Replace(content, @"(?mi)^\s*AllowedIPs\s*=.*$",
            "AllowedIPs = 0.0.0.0/1, 128.0.0.0/1, ::/1, 8000::/1");
        if (prepared == content)
            throw new InvalidOperationException("The selected file has no AllowedIPs entry.");

        prepared = Regex.Replace(prepared, @"(?mi)^\s*Endpoint\s*=.*$", $"Endpoint = {endpoint}:{port}");

        var directory = Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var outputPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(sourcePath)}-DualLink.conf");
        await File.WriteAllTextAsync(outputPath, prepared);

        Directory.CreateDirectory(StateDirectory);
        await File.WriteAllTextAsync(EndpointStatePath, endpoint.ToString());
        return new(outputPath, endpoint);
    }

    public IPAddress? LoadEndpoint()
    {
        try { return IPAddress.TryParse(File.ReadAllText(EndpointStatePath).Trim(), out var ip) ? ip : null; }
        catch { return null; }
    }

    private static string ExtractHost(string endpoint)
    {
        endpoint = endpoint.Trim();
        if (endpoint.StartsWith('['))
        {
            var end = endpoint.IndexOf(']');
            if (end > 1) return endpoint[1..end];
        }
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon] : endpoint;
    }

    private static int? ExtractPort(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon >= 0 && int.TryParse(endpoint[(colon + 1)..], out var port) && port is > 0 and <= 65535 ? port : null;
    }
}

public sealed record PreparedWireGuardConfig(string Path, IPAddress Endpoint);
