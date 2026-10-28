using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

public static class RegionStates
{
    /// <summary>
    /// A region's state from its program's process and what the launcher knows. <paramref name="loaded"/> is the region
    /// IDs the running program loads, or null when unknown (the region then counts as loaded).
    /// </summary>
    public static RegionState Derive(RegionSettings r, ComponentStatus? program, bool held, IReadOnlyCollection<Guid>? loaded, bool loginsEnabled)
    {
        if (r.Parked) return RegionState.Parked;
        if (held) return RegionState.Stopped;
        switch (program?.State)
        {
            case ProcessState.Running:
                if (loaded is not null && !loaded.Contains(r.Uuid)) return RegionState.Stopped;
                return loginsEnabled ? RegionState.Running : RegionState.Starting;
            case ProcessState.Starting: return RegionState.Starting;
            case ProcessState.Stopping: return RegionState.Stopping;
            case ProcessState.Failed or ProcessState.Crashed: return RegionState.Failed;
            default: return RegionState.Stopped;
        }
    }
}
