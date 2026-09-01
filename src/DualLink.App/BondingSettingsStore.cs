using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace DualLink.App;

internal sealed record SavedBondingSettings(string RelayAddress, string ProtectedKey, bool ServerReady = false);

internal static class BondingSettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "bonding.json");

    public static void Save(string relayAddress, byte[] key, bool serverReady = false)
    {
        Directory.CreateDirectory(DirectoryPath);
        var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        var settings = new SavedBondingSettings(relayAddress, Convert.ToBase64String(protectedKey), serverReady);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
    }

    public static (string RelayAddress, byte[] Key, bool ServerReady)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var settings = JsonSerializer.Deserialize<SavedBondingSettings>(File.ReadAllText(FilePath));
            if (settings is null) return null;
            var key = ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedKey), null, DataProtectionScope.CurrentUser);
            return (settings.RelayAddress, key, settings.ServerReady);
        }
        catch { return null; }
    }
}
