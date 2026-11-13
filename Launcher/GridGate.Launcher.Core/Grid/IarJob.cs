namespace GridGate.Launcher.Core.Grid;

/// <summary>What an IAR job does: save an avatar's whole inventory to a file, or load a file into a folder of it.</summary>
public enum IarJobKind { Save, Load }

/// <summary>
/// One IAR job for one avatar (spec 2026-11-11-iar-backups-design §6.1). <see cref="File"/> is a full path inside
/// <see cref="LauncherPaths.Iars"/>; <see cref="Folder"/> is, for a load, the folder directly under My Inventory to load into, which
/// must exist already (OpenSim's load refuses a missing one).
/// </summary>
public sealed record IarJobRequest(Guid Principal, string FirstName, string LastName, IarJobKind Kind, string File, string? Folder = null);

/// <summary>Where an IAR job is: the stage in plain words and, while a save runs, the bytes written so far.</summary>
public sealed record IarJobProgress(string Stage, long? BytesWritten);

/// <summary>The plain words an IAR job fails with.</summary>
public static class IarJobErrors
{
    public const string NoRegion = "No region is running to do this. Try again when one is running.";
    public const string Busy = "Every region is busy with another job. Try again in a few minutes.";
    public const string Failed = "The inventory job did not finish; see the region log.";
    public const string Stopped = "The region stopped before the inventory job finished.";
    public const string NotStarted = "The region did not start the inventory job; see the region log.";
}
