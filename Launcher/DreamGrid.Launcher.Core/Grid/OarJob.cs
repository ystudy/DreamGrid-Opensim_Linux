namespace DreamGrid.Launcher.Core.Grid;

/// <summary>What an OAR job does to a region: load a library file, save the region to the library, or load its own type again.</summary>
public enum OarJobKind { Import, Save, MakeLand }

/// <summary>One OAR job. <see cref="File"/> is the library name for <see cref="OarJobKind.Import"/>; it is null for the other kinds.</summary>
public sealed record OarJobRequest(Guid RegionId, OarJobKind Kind, string? File = null);

/// <summary>Where a job is: the stage in plain words and, while a save runs, the bytes written so far.</summary>
public sealed record OarJobProgress(string Stage, long? BytesWritten);
