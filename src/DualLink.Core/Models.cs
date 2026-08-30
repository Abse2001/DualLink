using System.Net;
using System.Net.NetworkInformation;

namespace DualLink.Core;

public sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    int InterfaceIndex,
    IPAddress? Address,
    IPAddress? Gateway,
    int? OriginalMetric);

public sealed record ProbeResult(
    string AdapterId,
    DateTimeOffset Timestamp,
    bool Online,
    double LatencyMs,
    double JitterMs,
    double PacketLossPercent,
    double Score,
    string? Error = null);

public sealed record FailoverDecision(
    string? ActiveAdapterId,
    bool Changed,
    string Reason);

public sealed record DualLinkSettings
{
    public bool AutoOptimize { get; init; } = true;
    public string ProbeHost { get; init; } = "1.1.1.1";
    public int ProbeIntervalSeconds { get; init; } = 1;
    public int SwitchConfirmationCount { get; init; } = 3;
    public double MinimumScoreImprovement { get; init; } = 12;
    public int PreferredMetric { get; init; } = 10;
    public int BackupMetric { get; init; } = 60;
}
