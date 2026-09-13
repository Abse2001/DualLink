namespace DualLink.Core;

/// <summary>
/// Gives a physical adapter one path number for the lifetime of the client.
/// Removing another adapter must never renumber or recreate surviving paths.
/// </summary>
public sealed class StablePathIdAllocator
{
    private readonly Dictionary<string, byte> _ids = new(StringComparer.OrdinalIgnoreCase);
    private byte _next = 1;

    public byte GetOrAdd(string adapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        if (_ids.TryGetValue(adapterId, out var existing)) return existing;
        if (_next == 0) throw new InvalidOperationException("No bonding path identifiers remain.");
        var assigned = _next++;
        _ids[adapterId] = assigned;
        return assigned;
    }
}
