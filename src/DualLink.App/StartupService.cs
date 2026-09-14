using Microsoft.Win32;
using System.Diagnostics;

namespace DualLink.App;

internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LinkWeaver";
    private const string LegacyValueName = "DualLink";
    private const string ScheduledTaskName = "LinkWeaver";

    public static bool IsEnabled()
    {
        var query = RunTaskScheduler("/Query", "/TN", ScheduledTaskName, "/XML");
        if (query.ExitCode == 0 &&
            query.Output.Contains("<Enabled>true</Enabled>", StringComparison.OrdinalIgnoreCase) &&
            query.Output.Contains(Path.GetFileName(GetExecutablePath()), StringComparison.OrdinalIgnoreCase))
            return true;

        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
        {
            if (key?.GetValue(ValueName) is not string && key?.GetValue(LegacyValueName) is not string)
                return false;
        }

        // Preserve the user's previous startup preference while moving the entry
        // from the old DualLink executable to LinkWeaver.
        SetEnabled(true);
        return true;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows startup settings could not be opened.");

        if (enabled)
        {
            var command = $"\"{GetExecutablePath()}\" --minimized";
            var result = RunTaskScheduler("/Create", "/TN", ScheduledTaskName, "/SC", "ONLOGON",
                "/TR", command, "/RL", "HIGHEST", "/F");
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Windows could not create the LinkWeaver startup task. {result.Error}".Trim());
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            RunTaskScheduler("/Delete", "/TN", ScheduledTaskName, "/F");
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }

    private static (int ExitCode, string Output, string Error) RunTaskScheduler(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static string GetExecutablePath() =>
        Environment.ProcessPath ?? throw new InvalidOperationException("The LinkWeaver executable path could not be determined.");
}
