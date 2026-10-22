using System.Net;
using System.Net.Sockets;

namespace DualLink.Core;

public static class WireGuardEndpointParser
{
    public static IPAddress? Parse(string output, IEnumerable<string> activeTunnelNames)
    {
        var names = activeTunnelNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // `wg show all endpoints` has three TAB-separated columns. Splitting all
        // whitespace discards otherwise valid tunnel names containing spaces.
        var endpoints = output.Split('\n')
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(fields => fields.Length == 3 && names.Contains(fields[0]))
            .Select(fields => IPEndPoint.TryParse(fields[2].Trim(), out var endpoint) ? endpoint : null)
            .Where(endpoint => endpoint is { Port: > 0 } && endpoint.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(endpoint => endpoint!.Address)
            .Distinct().ToArray();
        return endpoints.Length == 1 ? endpoints[0] : null;
    }
}
