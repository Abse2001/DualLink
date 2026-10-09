using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DualLink.Core;

public static class WireGuardConfigRewriter
{
    public static string Prepare(string content, IPAddress endpoint, int port)
    {
        if (endpoint.AddressFamily != AddressFamily.InterNetwork || port is < 1 or > 65535)
            throw new ArgumentException("An IPv4 WireGuard endpoint and valid UDP port are required.");
        if (Regex.Matches(content, @"(?mi)^[ \t]*\[Peer\][ \t]*\r?$").Count != 1)
            throw new InvalidOperationException("Select a single-peer Proton WireGuard configuration.");
        if (!Regex.IsMatch(content, @"(?mi)^[ \t]*AllowedIPs[ \t]*="))
            throw new InvalidOperationException("The selected file has no AllowedIPs entry.");
        if (!Regex.IsMatch(content, @"(?mi)^[ \t]*Endpoint[ \t]*="))
            throw new InvalidOperationException("The selected file has no Endpoint entry.");
        var prepared = Regex.Replace(content, @"(?mi)^[ \t]*AllowedIPs[ \t]*=[^\r\n]*",
            "AllowedIPs = 0.0.0.0/1, 128.0.0.0/1, ::/1, 8000::/1");
        prepared = Regex.Replace(prepared, @"(?mi)^[ \t]*Endpoint[ \t]*=[^\r\n]*", $"Endpoint = {endpoint}:{port}");
        if (Regex.IsMatch(prepared, @"(?mi)^[ \t]*PersistentKeepalive[ \t]*="))
            return Regex.Replace(prepared, @"(?mi)^[ \t]*PersistentKeepalive[ \t]*=[^\r\n]*", "PersistentKeepalive = 2");
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        return Regex.Replace(prepared, @"(?mi)^([ \t]*Endpoint[ \t]*=[^\r\n]*)", "$1" + newline + "PersistentKeepalive = 2");
    }
}
