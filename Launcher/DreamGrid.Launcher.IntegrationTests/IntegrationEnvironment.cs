namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>Locates the external programs the integration tests need.</summary>
public static class IntegrationEnvironment
{
    /// <summary>The MariaDB bin directory, or null when none is available.</summary>
    public static string? MariaDbBin
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DREAMGRID_MARIADB_BIN");
            if (!string.IsNullOrWhiteSpace(env))
            {
                // "/usr/sbin" only counts as present when the server is really there.
                if (env != "/usr/sbin" || File.Exists("/usr/sbin/mariadbd"))
                    return env;
                return null;
            }
            return !OperatingSystem.IsWindows() && File.Exists("/usr/sbin/mariadbd") ? "/usr/sbin" : null;
        }
    }

    /// <summary>The repo's <c>bin/</c> directory (must contain OpenSim.dll).</summary>
    public static string OpenSimBin
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var bin = Path.Combine(dir.FullName, "bin");
                if (File.Exists(Path.Combine(bin, "OpenSim.dll")))
                    return bin;
            }
            throw new DirectoryNotFoundException("Could not find the repo bin/ directory containing OpenSim.dll.");
        }
    }
}
