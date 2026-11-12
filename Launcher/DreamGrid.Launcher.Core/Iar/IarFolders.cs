namespace DreamGrid.Launcher.Core.Iar;

/// <summary>The inventory archive folders (spec 2026-11-11-iar-backups-design §6.2).</summary>
public static class IarFolders
{
    /// <summary>
    /// Creates every IAR folder owner-only (IARs hold residents' inventory), then empties the two staging folders: uploads and
    /// saves cut off by the last stop leave parts behind that nothing is using. Ready and staff files are kept.
    /// </summary>
    public static void Prepare(LauncherPaths paths)
    {
        foreach (var dir in new[] { paths.Iars, paths.IarsIncoming, paths.IarsSaving, paths.IarsReady, paths.IarsStaff })
            LauncherPaths.CreateOwnerOnly(dir);
        foreach (var dir in new[] { paths.IarsIncoming, paths.IarsSaving })
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                try
                {
                    if (entry is DirectoryInfo d) d.Delete(true); else entry.Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
