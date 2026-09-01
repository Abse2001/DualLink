using System.Text.Json;
using System.IO;

namespace DualLink.App;

public sealed record ConnectionHistorySample(DateTimeOffset Timestamp, string Connection, string Type, bool Online, double Quality, double LatencyMs);
public sealed record ConnectionHistoryEvent(DateTimeOffset Timestamp, string Connection, string Event, double? DurationSeconds);
public sealed record ConnectionHistoryData(List<ConnectionHistorySample> Samples, List<ConnectionHistoryEvent> Events);

public static class ConnectionHistoryStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "connection-history.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static ConnectionHistoryData Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new([], []);
            return JsonSerializer.Deserialize<ConnectionHistoryData>(File.ReadAllText(FilePath), JsonOptions) ?? new([], []);
        }
        catch
        {
            return new([], []);
        }
    }

    public static void Save(ConnectionHistoryData data)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporary, FilePath, true);
    }
}
