using Microsoft.Win32;

namespace DualLink.App;

internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LinkWeaver";
    private const string LegacyValueName = "DualLink";

    public static bool IsEnabled()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
        {
            if (key?.GetValue(ValueName) is string command &&
                command.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase)) return true;
            if (key?.GetValue(LegacyValueName) is not string) return false;
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
            key.SetValue(ValueName, $"\"{GetExecutablePath()}\" --minimized", RegistryValueKind.String);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }

    private static string GetExecutablePath() =>
        Environment.ProcessPath ?? throw new InvalidOperationException("The LinkWeaver executable path could not be determined.");
}
