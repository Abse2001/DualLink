using System.Diagnostics;
using System.Net;

namespace DualLink.App;

internal sealed class ServerProvisioner
{
    public async Task ProvisionAsync(IPAddress relay, string privateKeyPath, byte[] bondingKey, IProgress<string> progress, CancellationToken token)
    {
        if (!File.Exists(privateKeyPath)) throw new FileNotFoundException("The AWS private key file was not found.", privateKeyPath);
        var relayDirectory = Path.Combine(AppContext.BaseDirectory, "relay");
        string[] required = ["DualLink.Relay", "install-relay.sh", "duallink-relay.service"];
        foreach (var file in required)
            if (!File.Exists(Path.Combine(relayDirectory, file))) throw new FileNotFoundException($"The installer is missing relay/{file}.");

        var ssh = FindOpenSsh("ssh.exe");
        var scp = FindOpenSsh("scp.exe");
        var destination = $"ubuntu@{relay}";
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"duallink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var environmentFile = Path.Combine(temporaryDirectory, "relay.env");
        await File.WriteAllTextAsync(environmentFile, $"DUALLINK_KEY={Convert.ToBase64String(bondingKey)}\nDUALLINK_PORT=443\n", token);

        try
        {
            progress.Report("Creating the private installation directory on the relay…");
            await RunAsync(ssh, CommonArguments(privateKeyPath, destination).Concat([destination, "mkdir -p /tmp/duallink-install && chmod 700 /tmp/duallink-install"]), token);

            progress.Report("Uploading the signed DualLink relay package…");
            var uploadArguments = CommonArguments(privateKeyPath, destination)
                .Concat(required.Select(file => Path.Combine(relayDirectory, file)))
                .Concat([environmentFile, $"{destination}:/tmp/duallink-install/"]);
            await RunAsync(scp, uploadArguments, token);

            progress.Report("Installing and starting the encrypted relay…");
            const string install = "sudo bash /tmp/duallink-install/install-relay.sh /tmp/duallink-install/DualLink.Relay && " +
                "sudo install -o root -g duallink -m 0640 /tmp/duallink-install/relay.env /etc/duallink/relay.env && " +
                "sudo systemctl restart duallink-relay.service && sudo systemctl is-active duallink-relay.service";
            var result = await RunAsync(ssh, CommonArguments(privateKeyPath, destination).Concat([destination, install]), token);
            if (!result.Contains("active", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The relay service did not become active.");
            progress.Report("Relay installed and active.");
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, true); } catch { }
        }
    }

    private static IEnumerable<string> CommonArguments(string keyPath, string destination) =>
        ["-i", keyPath, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=15"];

    private static string FindOpenSsh(string executable)
    {
        var systemPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", executable);
        return File.Exists(systemPath) ? systemPath : executable;
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"{Path.GetFileName(executable)} failed with exit code {process.ExitCode}." : error.Trim());
        return output;
    }
}
