using DreamGrid.Launcher.Core.Processes;

namespace DreamGrid.Launcher.Core.Grid;

public sealed record ComponentStatus(string Name, ProcessState State, int? Pid, TimeSpan? Uptime, long? MemoryBytes, string? LastError);

/// <summary>Why the last start failed. <see cref="Message"/> is raw (it may name paths); <see cref="ConflictPort"/> is set for a port conflict.</summary>
public sealed record StartFailure(string Message, int? ConflictPort);

/// <summary>The grid operation under way, if any (between components every state can look settled).</summary>
public enum GridActivity { None, Starting, Stopping, Restarting }

/// <summary>
/// <see cref="LoginUri"/> is <c>http://{host}:{RobustPublicPort}</c>; <see cref="Region"/> is null until a region exists.
/// <see cref="StartFailure"/> is the last failed start, cleared by a successful one.
/// </summary>
public sealed record GridStatus(ComponentStatus Database, ComponentStatus Robust, ComponentStatus? Region, bool LoginsEnabled, string LoginUri,
    StartFailure? StartFailure = null, GridActivity Activity = GridActivity.None);
