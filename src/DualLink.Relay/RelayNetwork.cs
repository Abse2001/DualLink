using System.Diagnostics;

namespace DualLink.Relay;

internal static class RelayNetwork
{
    public static async Task ConfigureAsync(CancellationToken token)
    {
        await RunIpAsync(token, "address", "replace", "10.77.0.1/24", "dev", "dlbond0");
        await RunIpAsync(token, "link", "set", "dev", "dlbond0", "mtu", "1380", "up");
    }

    private static async Task RunIpAsync(CancellationToken token, params string[] arguments)
    {
        var info = new ProcessStartInfo("/usr/sbin/ip")
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start iproute2.");
        var error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException($"ip command failed: {error.Trim()}");
    }
}
