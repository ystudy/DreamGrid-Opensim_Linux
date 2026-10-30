using DreamGrid.Launcher.Core.Processes;

namespace DreamGrid.Launcher.Core.Grid;

public sealed record ComponentStatus(string Name, ProcessState State, int? Pid, TimeSpan? Uptime, long? MemoryBytes, string? LastError);

/// <summary>Why the last start failed. <see cref="Message"/> is raw (it may name paths); <see cref="ConflictPort"/> is set for a port conflict.</summary>
public sealed record StartFailure(string Message, int? ConflictPort);

/// <summary>The grid operation under way, if any (between components every state can look settled).</summary>
public enum GridActivity { None, Starting, Stopping, Restarting }

/// <summary>What a region is doing. A region whose program runs but doesn't load it (held or not loaded yet) is <see cref="Stopped"/>.</summary>
public enum RegionState { Stopped, Starting, Running, Stopping, Failed, Parked }

/// <summary>One region; <see cref="RunsWith"/> names the other live regions in its program.</summary>
public sealed record RegionStatus(Guid Id, string Name, string Instance, RegionState State, bool LoginsEnabled, int X, int Y, int Size,
    IReadOnlyList<string> RunsWith);

/// <summary>One OpenSim program and the regions it hosts.</summary>
public sealed record ProgramStatus(string Instance, ComponentStatus Process, IReadOnlyList<Guid> Regions);

/// <summary>A region's power menu (spec §6.8).</summary>
public enum RegionPower { Start, Stop, Restart, Park, Unpark, UnparkAndStart }

/// <summary>
/// A region to add. <see cref="Content"/> is "starter" or "island"; <see cref="ShareWith"/> is a program's instance, or
/// null for a program of its own.
/// </summary>
public sealed record NewRegion(string Name, int X, int Y, int Size, string Content, string? ShareWith);

/// <summary>
/// <see cref="LoginUri"/> is <c>http://{host}:{RobustPublicPort}</c>. <see cref="LoginsEnabled"/>: see
/// <see cref="ComputeLoginsEnabled"/>. <see cref="StartFailure"/> is the last failed start, cleared by a successful one.
/// <see cref="RobustOutdated"/>: Robust runs with a world name other than the saved one (the world was named while it
/// started); restarting it applies the name. <see cref="RunsRobust"/> is false on a plan that uses another computer's grid
/// service (a Region Server or a Connected Server): <see cref="Robust"/> then describes that grid service at
/// <see cref="OtherGridAddress"/> (the Grid Asset Server's <c>host:port</c>, or the other grid's address), Running when
/// its last check answered, Failed when it didn't and Stopped before the first check.
/// </summary>
public sealed record GridStatus(ComponentStatus Database, ComponentStatus Robust, IReadOnlyList<ProgramStatus> Programs,
    IReadOnlyList<RegionStatus> Regions, bool LoginsEnabled, string LoginUri, StartFailure? StartFailure = null,
    GridActivity Activity = GridActivity.None, bool RobustOutdated = false, bool RunsRobust = true, string? OtherGridAddress = null)
{
    /// <summary>
    /// True when at least one region counts and every counted region runs with logins enabled; parked regions and
    /// <paramref name="held"/> ones (stopped until the next world start) don't count.
    /// </summary>
    public static bool ComputeLoginsEnabled(IReadOnlyList<RegionStatus> regions, IReadOnlySet<Guid> held)
    {
        var counted = regions.Where(r => r.State != RegionState.Parked && !held.Contains(r.Id)).ToList();
        return counted.Count > 0 && counted.All(r => r.State == RegionState.Running && r.LoginsEnabled);
    }
}
