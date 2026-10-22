using System.Net;
using System.Net.Sockets;

namespace DualLink.Core;

public sealed record WireGuardPeerEndpoint(string TunnelName, string PublicKey, IPEndPoint Endpoint)
{
    public static WireGuardPeerEndpoint? FindUnique(string output, IEnumerable<string> activeTunnelNames,
        IPAddress expectedEndpoint)
    {
        if (expectedEndpoint.AddressFamily != AddressFamily.InterNetwork) return null;
        var names = activeTunnelNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = new List<WireGuardPeerEndpoint>();
        foreach (var line in output.Split('\n'))
        {
            // wg's machine-readable output uses tabs, so spaces in aliases are
            // part of the alias rather than additional columns.
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 3 || !names.Contains(fields[0]) ||
                !IPEndPoint.TryParse(fields[2], out var endpoint) || endpoint.Port == 0 ||
                endpoint.Address.AddressFamily != AddressFamily.InterNetwork ||
                !endpoint.Address.Equals(expectedEndpoint)) continue;

            // Fail closed for ambiguous peers, including a malformed key on a
            // second matching line. Never guess which running peer to modify.
            if (!IsPublicKey(fields[1])) return null;
            matches.Add(new(fields[0], fields[1], endpoint));
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool IsPublicKey(string value)
    {
        Span<byte> bytes = stackalloc byte[32];
        return value.Length == 44 && Convert.TryFromBase64String(value, bytes, out var count) &&
            count == 32 && Convert.ToBase64String(bytes) == value;
    }
}
