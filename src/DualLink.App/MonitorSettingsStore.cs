using System.IO;
using System.Text.Json;

namespace DualLink.App;

internal sealed record MonitorSettings(string? PreferredId, string Response, bool AutoRoutes, bool ProtonSafe, string BondingMode);

internal static class MonitorSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DualLink", "monitor-settings.json");
    public static MonitorSettings? Load()
    {
        try { return JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(SettingsPath)); }
        catch { return null; }
    }
    public static void Save(MonitorSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(settings));
            File.Move(SettingsPath + ".tmp", SettingsPath, overwrite: true);
        }
        catch (Exception error) { AppLog.Write($"Unable to save monitor preferences: {error.Message}"); }
    }
}
