using System.Globalization;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>
/// Whether a restart is offered for a region (<see cref="Shown"/>), whether the person may use it now (<see cref="Allowed"/>,
/// else <see cref="Reason"/> says why) and what it does (<see cref="Action"/>, null when not shown).
/// </summary>
public sealed record RestartOffer(bool Shown, bool Allowed, string? Reason, RegionPower? Action)
{
    public static readonly RestartOffer None = new(false, false, null, null);
}

/// <summary>What a region owner may do about a stuck region. Pure: the caller supplies the status, the owners and the clock.</summary>
public static class RegionOwnerRules
{
    /// <summary>How long after a restart the same region can be restarted again.</summary>
    public static readonly TimeSpan RestartGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The restart offered to <paramref name="owner"/> for <paramref name="region"/>. It shows only for a stuck region: failed, or
    /// stopped (not parked) while the grid service runs. <paramref name="ownerOf"/> gives the person holding a region's rights, or
    /// null for a region nobody holds; <paramref name="lastRestart"/> is when this region was last restarted this way, if it was.
    /// </summary>
    public static RestartOffer Restart(GridStatus status, RegionStatus region, Guid owner, Func<Guid, Guid?> ownerOf,
        DateTimeOffset? lastRestart, DateTimeOffset now)
    {
        RegionPower action;
        switch (region.State)
        {
            case RegionState.Failed:
                action = RegionPower.Restart;
                break;
            case RegionState.Stopped when status.Robust.State == ProcessState.Running:
                action = RegionPower.Start;
                break;
            default:
                return RestartOffer.None;
        }

        var shared = status.Programs.FirstOrDefault(p => p.Regions.Contains(region.Id))?.Regions
            .Any(other => other != region.Id && ownerOf(other) != owner) ?? false;
        if (shared)
            return new RestartOffer(true, false, "This region runs with regions that aren't yours. To restart it, contact the grid's staff.", action);

        if (lastRestart is { } last && now - last < RestartGap)
        {
            var again = (last + RestartGap).UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            return new RestartOffer(true, false, $"This region was restarted a few minutes ago. Try again after {again} (UTC).", action);
        }

        return new RestartOffer(true, true, null, action);
    }
}
