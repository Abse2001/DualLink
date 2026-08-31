using System.Security.Cryptography;
using System.Text.Json;

namespace DualLink.App;

internal sealed record SavedBondingSettings(string RelayAddress, string ProtectedKey);

internal static class BondingSettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "bonding.json");

    public static void Save(string relayAddress, byte[] key)
    {
        Directory.CreateDirectory(DirectoryPath);
        var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        var settings = new SavedBondingSettings(relayAddress, Convert.ToBase64String(protectedKey));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
    }

    public static (string RelayAddress, byte[] Key)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var settings = JsonSerializer.Deserialize<SavedBondingSettings>(File.ReadAllText(FilePath));
            if (settings is null) return null;
            var key = ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedKey), null, DataProtectionScope.CurrentUser);
            return (settings.RelayAddress, key);
        }
        catch { return null; }
    }
}
