namespace DreamGrid.Launcher.Core.Grid;

public static class StarterContent
{
    /// <summary>Path of the owner-supplied starter region OAR, or null when it has not been provided.</summary>
    public static string? StarterOar(string programRoot)
    {
        var path = Path.Combine(programRoot, "content", "starter-region.oar");
        return File.Exists(path) ? path : null;
    }

    /// <summary>As above, in the program files that hold the OpenSim program folder.</summary>
    public static string? StarterOar(LauncherPaths paths) => StarterOar(ProgramRoot(paths));

    /// <summary>The folder above the OpenSim program folder.</summary>
    public static string ProgramRoot(LauncherPaths paths) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.OpenSimProgram))) ?? paths.OpenSimProgram;
}
