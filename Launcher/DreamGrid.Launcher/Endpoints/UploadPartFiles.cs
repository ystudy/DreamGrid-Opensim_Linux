namespace DreamGrid.Launcher.Endpoints;

/// <summary>Creates the part file an upload is written to in <c>.incoming</c>; tests swap it to make the writes fail.</summary>
public class UploadPartFiles
{
    /// <summary>A new, empty file at the path, for writing only.</summary>
    public virtual Stream Create(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
}
