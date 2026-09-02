using System.Diagnostics;
using System.Net;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

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
        var temporaryDirectory = CreatePrivateTemporaryDirectory();
        var environmentFile = Path.Combine(temporaryDirectory, "relay.env");
        var provisionFile = Path.Combine(temporaryDirectory, "provision-relay.sh");
        await File.WriteAllTextAsync(environmentFile, $"DUALLINK_KEY={Convert.ToBase64String(bondingKey)}\nDUALLINK_PORT=443\n", token);
        const string provisionScript = """
#!/usr/bin/env bash
set -Eeuo pipefail

diagnostics() {
  rc=$?
  set +e
  echo '--- DualLink relay diagnostics ---'
  systemctl status duallink-relay.service --no-pager -l
  journalctl -u duallink-relay.service -n 40 --no-pager
  exit "$rc"
}
trap diagnostics ERR
cleanup() { rm -rf -- /tmp/duallink-install; }
trap cleanup EXIT

sed -i 's/\r$//' /tmp/duallink-install/install-relay.sh /tmp/duallink-install/duallink-relay.service
bash /tmp/duallink-install/install-relay.sh /tmp/duallink-install/DualLink.Relay
install -o root -g duallink -m 0640 /tmp/duallink-install/relay.env /etc/duallink/relay.env
systemctl restart duallink-relay.service
sleep 2
systemctl is-active duallink-relay.service
ss -lunp | grep -q ':443 '
""";
        await File.WriteAllTextAsync(provisionFile, provisionScript.Replace("\r\n", "\n"), token);

        try
        {
            progress.Report("Creating the private installation directory on the relay…");
            await RunAsync(ssh, CommonArguments(privateKeyPath, destination).Concat([destination, "mkdir -p /tmp/duallink-install && chmod 700 /tmp/duallink-install"]), token);

            progress.Report("Verifying and uploading the DualLink relay package…");
            VerifyRelayPackage(relayDirectory, required);
            var uploadArguments = CommonArguments(privateKeyPath, destination)
                .Concat(required.Select(file => Path.Combine(relayDirectory, file)))
                .Concat([environmentFile, provisionFile, $"{destination}:/tmp/duallink-install/"]);
            await RunAsync(scp, uploadArguments, token);

            progress.Report("Installing and starting the encrypted relay…");
            var result = await RunAsync(ssh, CommonArguments(privateKeyPath, destination)
                .Concat([destination, "sudo bash /tmp/duallink-install/provision-relay.sh"]), token);
            if (!result.Contains("active", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The relay service did not become active or listen on UDP 443.");
            progress.Report("Relay installed and active.");
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, true); } catch { }
        }
    }

    private static IEnumerable<string> CommonArguments(string keyPath, string destination) =>
        ["-i", keyPath, "-o", "BatchMode=yes", "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
         "-o", "IdentitiesOnly=yes", "-o", "ForwardAgent=no", "-o", "ClearAllForwardings=yes",
         "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=15",
         "-o", "ConnectionAttempts=1", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2"];

    private static string CreatePrivateTemporaryDirectory()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink", "Temp");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var identity = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Unable to identify the current Windows user.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.CreateDirectory(path, security);
        return path;
    }

    private static void VerifyRelayPackage(string relayDirectory, IEnumerable<string> files)
    {
        var manifestPath = Path.Combine(relayDirectory, "SHA256SUMS.txt");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("The relay integrity manifest is missing.");
        var expected = File.ReadLines(manifestPath)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!expected.TryGetValue(file, out var hash)) throw new InvalidDataException($"The integrity manifest does not contain {file}.");
            using var stream = File.OpenRead(Path.Combine(relayDirectory, file));
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(hash)))
                throw new InvalidDataException($"Relay package integrity check failed for {file}.");
        }
    }

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
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timeout = Path.GetFileName(executable).Equals("scp.exe", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(10)
            : TimeSpan.FromSeconds(45);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0} seconds. Check that the EC2 instance is running and SSH TCP 22 is reachable.");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, new[] { error.Trim(), output.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"{Path.GetFileName(executable)} failed with exit code {process.ExitCode}."
                : details);
        }
        return output;
    }
}
