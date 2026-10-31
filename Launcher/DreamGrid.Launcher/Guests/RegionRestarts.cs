namespace DreamGrid.Launcher.Guests;

/// <summary>
/// When each region was last restarted by its owner, kept in memory only: a launcher restart clears it, which only means an owner
/// can restart once more. Safe to call from any thread.
/// </summary>
public sealed class RegionRestarts
{
    private readonly Dictionary<Guid, DateTimeOffset> _last = [];
    private readonly Lock _gate = new();

    /// <summary>The time of the region's last accepted restart, or null.</summary>
    public DateTimeOffset? Last(Guid region)
    {
        lock (_gate) return _last.TryGetValue(region, out var at) ? at : null;
    }

    /// <summary>
    /// Asks <paramref name="allowed"/> about the region's last restart and, when it says yes, records <paramref name="now"/> as the
    /// new one, in one step: two posts at once can't both be accepted.
    /// </summary>
    public bool TryBegin(Guid region, DateTimeOffset now, Func<DateTimeOffset?, bool> allowed)
    {
        lock (_gate)
        {
            if (!allowed(_last.TryGetValue(region, out var at) ? at : null)) return false;
            _last[region] = now;
            return true;
        }
    }
}
