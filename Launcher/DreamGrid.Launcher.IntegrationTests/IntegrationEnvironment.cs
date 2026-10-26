using Xunit;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>
/// The test classes that start MariaDB, Robust, regions or the launcher: they share ports (2b saw a port-contention
/// flake), so they run one at a time and never beside another collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GridCollection
{
    public const string Name = "Grid";
}

/// <summary>Locates the external programs the integration tests need.</summary>
public static class IntegrationEnvironment
{
    /// <summary>
    /// DREAMGRID_REQUIRE_INTEGRATION=1 (set in CI) turns a missing prerequisite into a failure instead of a skip,
    /// so a CI job whose MariaDB or bin/ went missing cannot pass by skipping everything.
    /// </summary>
    public static bool Required => Environment.GetEnvironmentVariable("DREAMGRID_REQUIRE_INTEGRATION") == "1";

    /// <summary>Skips the test when a prerequisite is missing, or fails it when <see cref="Required"/>.</summary>
    public static void SkipWhenMissing(bool missing, string reason)
    {
        if (!missing) return;
        if (Required) Assert.Fail(reason + " DREAMGRID_REQUIRE_INTEGRATION=1, so this is a failure, not a skip.");
        Assert.Skip(reason);
    }

    /// <summary>The MariaDB bin directory; skips (or fails, see <see cref="Required"/>) when none is available.</summary>
    public static string RequireMariaDbBin()
    {
        var bin = MariaDbBin;
        SkipWhenMissing(bin is null, "No MariaDB binaries (set DREAMGRID_MARIADB_BIN).");
        return bin!;
    }

    /// <summary>The repo's built <c>bin/</c>; skips (or fails, see <see cref="Required"/>) when it is not built.</summary>
    public static string RequireOpenSimBin()
    {
        string? bin = null;
        try { bin = OpenSimBin; }
        catch (DirectoryNotFoundException) { }
        SkipWhenMissing(bin is null, "No built bin/ with OpenSim.dll.");
        return bin!;
    }

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

    /// <summary>
    /// The built launcher. The project reference copies DreamGrid.Launcher.dll with its own runtimeconfig and deps
    /// files next to the tests, so <c>dotnet &lt;this&gt;</c> runs the real host.
    /// </summary>
    public static string LauncherDll => Path.Combine(AppContext.BaseDirectory, "DreamGrid.Launcher.dll");
}
