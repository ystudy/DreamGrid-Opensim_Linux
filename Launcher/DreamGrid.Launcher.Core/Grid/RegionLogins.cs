using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// Whether each region accepts logins, from RegionReady alerts and console answers. Each region counts its alerts, so a
/// console answer that was on its way when an alert arrived never overwrites the newer alert.
/// </summary>
public sealed class RegionLogins
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, bool> _enabled = new();
    private readonly Dictionary<Guid, long> _alerts = new();

    /// <summary>
    /// Applies a "logins enabled|disabled" alert to the region it names: by ID, else by name ignoring case. Returns false
    /// (and changes nothing) for a region not in <paramref name="regions"/>.
    /// </summary>
    public bool Apply(RegionAlert alert, IReadOnlyList<RegionSettings> regions)
    {
        var region = regions.FirstOrDefault(r => alert.RegionId != Guid.Empty && r.Uuid == alert.RegionId)
            ?? regions.FirstOrDefault(r => r.Name.Equals(alert.RegionName, StringComparison.OrdinalIgnoreCase));
        if (region is null) return false;
        lock (_gate)
        {
            _alerts[region.Uuid] = Version(region.Uuid) + 1;
            _enabled[region.Uuid] = alert.Login.Equals("enabled", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    /// <summary>How many alerts the region has had; pass it to <see cref="SetIfUnchanged"/>.</summary>
    public long Version(Guid id)
    {
        lock (_gate) return _alerts.GetValueOrDefault(id);
    }

    /// <summary>Sets the region's state from a console answer, unless an alert arrived since <paramref name="seen"/>.</summary>
    public void SetIfUnchanged(Guid id, long seen, bool enabled)
    {
        lock (_gate)
            if (_alerts.GetValueOrDefault(id) == seen) _enabled[id] = enabled;
    }

    public bool Enabled(Guid id)
    {
        lock (_gate) return _enabled.GetValueOrDefault(id);
    }

    /// <summary>The regions went down (their program stopped or restarted): they accept no logins until told again.</summary>
    public void Clear(IEnumerable<Guid> ids)
    {
        lock (_gate)
            foreach (var id in ids) _enabled[id] = false;
    }
}
