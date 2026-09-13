using System.Text.Json;
using System.IO;

namespace DualLink.App;

public sealed record ConnectionHistorySample(
    DateTimeOffset Timestamp,
    string Connection,
    string Type,
    bool Online,
    double Quality,
    double LatencyMs,
    double DownloadMbps = 0,
    double UploadMbps = 0,
    string State = "Unknown",
    bool LinkUp = false,
    bool HasIpv4Address = false,
    bool HasGateway = false,
    string Address = "",
    string Gateway = "",
    string ProbeError = "",
    bool ActivePath = false,
    bool WireGuardActive = false,
    bool BondingActive = false,
    string PublicIp = "");
public sealed record ConnectionHistoryEvent(
    DateTimeOffset Timestamp,
    string Connection,
    string Event,
    double? DurationSeconds,
    string Details = "");
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

    public static void ExportCsv(string path, IEnumerable<ConnectionHistorySample> samples, IEnumerable<ConnectionHistoryEvent> events)
    {
        static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        writer.WriteLine("Timestamp,Connection,Type,State,LinkUp,InternetReachable,HasIpv4Address,HasGateway,Address,Gateway,Quality,LatencyMs,DownloadMbps,UploadMbps,ActivePath,WireGuardActive,BondingActive,PublicIp,ProbeError");
        foreach (var item in samples.OrderBy(x => x.Timestamp))
            writer.WriteLine(string.Join(',', item.Timestamp.ToString("O"), Escape(item.Connection), Escape(item.Type),
                Escape(item.State), item.LinkUp.ToString(), item.Online.ToString(), item.HasIpv4Address.ToString(),
                item.HasGateway.ToString(), Escape(item.Address), Escape(item.Gateway),
                item.Quality.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                item.LatencyMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                item.DownloadMbps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                item.UploadMbps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                item.ActivePath.ToString(), item.WireGuardActive.ToString(), item.BondingActive.ToString(), Escape(item.PublicIp),
                Escape(item.ProbeError)));
        writer.WriteLine();
        writer.WriteLine("EventTimestamp,Connection,Event,DurationSeconds,Details");
        foreach (var item in events.OrderBy(x => x.Timestamp))
            writer.WriteLine(string.Join(',', item.Timestamp.ToString("O"), Escape(item.Connection), Escape(item.Event),
                item.DurationSeconds?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                Escape(item.Details)));
    }
}
