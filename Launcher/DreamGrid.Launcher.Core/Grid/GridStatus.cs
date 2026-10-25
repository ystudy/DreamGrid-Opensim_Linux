using DreamGrid.Launcher.Core.Processes;

namespace DreamGrid.Launcher.Core.Grid;

public sealed record ComponentStatus(string Name, ProcessState State, int? Pid, TimeSpan? Uptime, long? MemoryBytes, string? LastError);

/// <summary><see cref="LoginUri"/> is <c>http://{host}:{RobustPublicPort}</c>; <see cref="Region"/> is null until a region exists.</summary>
public sealed record GridStatus(ComponentStatus Database, ComponentStatus Robust, ComponentStatus? Region, bool LoginsEnabled, string LoginUri);
