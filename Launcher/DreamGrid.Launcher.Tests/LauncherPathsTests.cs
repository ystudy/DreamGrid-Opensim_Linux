using System.Runtime.Versioning;
using DreamGrid.Launcher.Core;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class LauncherPathsTests
{
    private static string Sep(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public void Resolve_prefers_args_over_env_over_default()
    {
        var env = new Dictionary<string, string?>
        {
            ["DREAMGRID_DATA"] = "/env/data",
            ["DREAMGRID_OPENSIM"] = "/env/opensim",
            ["DREAMGRID_MARIADB_BIN"] = "/env/maria",
            ["ProgramData"] = "/pd",
        };

        var fromArgs = LauncherPaths.Resolve(
            ["--data", "/arg/data", "--opensim", "/arg/opensim", "--mariadb-bin", "/arg/maria"], env, isWindows: false);
        Assert.Equal(Path.GetFullPath("/arg/data"), fromArgs.Data);
        Assert.Equal(Path.GetFullPath("/arg/opensim"), fromArgs.OpenSimProgram);
        Assert.Equal(Path.GetFullPath("/arg/maria"), fromArgs.MariaDbBin);

        var fromEnv = LauncherPaths.Resolve([], env, isWindows: false);
        Assert.Equal(Path.GetFullPath("/env/data"), fromEnv.Data);
        Assert.Equal(Path.GetFullPath("/env/opensim"), fromEnv.OpenSimProgram);
        Assert.Equal(Path.GetFullPath("/env/maria"), fromEnv.MariaDbBin);

        var defaults = LauncherPaths.Resolve([], new Dictionary<string, string?> { ["DREAMGRID_OPENSIM"] = "/o" }, isWindows: false);
        Assert.Equal(Path.GetFullPath("/var/lib/dreamgrid"), defaults.Data);
        Assert.Null(defaults.MariaDbBin);
    }

    [Theory]
    [InlineData("--data")]
    [InlineData("--opensim")]
    [InlineData("--mariadb-bin")]
    public void Resolve_trailing_flag_without_value_throws(string flag)
    {
        var env = new Dictionary<string, string?> { ["DREAMGRID_OPENSIM"] = "/o" };
        var e = Assert.Throws<ArgumentException>(() => LauncherPaths.Resolve(["--other", flag], env, isWindows: false));
        Assert.Contains(flag, e.Message);
        Assert.Contains("needs a value", e.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_windows_without_ProgramData_uses_C_ProgramData(string? programData)
    {
        var env = new Dictionary<string, string?> { ["DREAMGRID_OPENSIM"] = "/o" };
        if (programData is not null) env["ProgramData"] = programData;
        Assert.Equal(Path.Combine(@"C:\ProgramData", "DreamGrid"),LauncherPaths.ResolveData([], env, isWindows: true));
    }

    [Fact]
    public void Resolve_throws_naming_opensim_when_unset()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LauncherPaths.Resolve([], new Dictionary<string, string?>(), isWindows: false));
        Assert.Contains("--opensim", ex.Message);
        Assert.Contains("DREAMGRID_OPENSIM", ex.Message);
    }

    [Fact]
    public void Default_data_dir_is_ProgramData_on_windows_and_var_lib_on_linux()
    {
        var env = new Dictionary<string, string?>
        {
            ["ProgramData"] = Path.Combine(Path.GetTempPath(), "PD"),
            ["DREAMGRID_OPENSIM"] = "/o",
        };

        Assert.Equal(Path.Combine(env["ProgramData"]!, "DreamGrid"), LauncherPaths.Resolve([], env, isWindows: true).Data);
        Assert.Equal(Path.GetFullPath("/var/lib/dreamgrid"), LauncherPaths.Resolve([], env, isWindows: false).Data);
    }

    [Fact]
    public void RegionDir_and_RegionDefinitionDir_follow_the_layout()
    {
        var p = new LauncherPaths("/d", "/o", null);
        Assert.EndsWith(Sep("state/regions/Welcome"), p.RegionDir("Welcome"));
        Assert.EndsWith(Sep("state/regions/Welcome/Region"), p.RegionDefinitionDir("Welcome"));
    }

    [Fact]
    public void EnsureCreated_handles_spaces_and_non_ascii()
    {
        var data = Path.Combine(Path.GetTempPath(), $"Dréam Grid {Guid.NewGuid():N}");
        try
        {
            var p = new LauncherPaths(data, "/o", null);
            p.EnsureCreated();

            string[] dirs =
            [
                p.Data, p.MariaDbData, p.MariaDbTmp, p.RuntimeOpenSim, p.RegionsRoot, p.AssetCache, p.FsAssetsData,
                p.FsAssetsSpool, p.ScriptEngines, p.Logs, p.Pids,
            ];
            foreach (var d in dirs)
                Assert.True(Directory.Exists(d), d);
            Assert.Equal(Path.Combine(data, "settings.json"), p.SettingsFile);
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public void Resolve_makes_relative_paths_absolute()
    {
        // A relative --data would otherwise follow the working directory of whichever child uses it.
        var env = new Dictionary<string, string?> { ["DREAMGRID_DATA"] = "rel-env-data", ["DREAMGRID_OPENSIM"] = "rel-env-opensim" };

        var fromArgs = LauncherPaths.Resolve(["--data", "rel-data", "--opensim", "rel-opensim", "--mariadb-bin", "rel-maria"], env, OperatingSystem.IsWindows());
        Assert.Equal(Path.GetFullPath("rel-data"), fromArgs.Data);
        Assert.Equal(Path.GetFullPath("rel-opensim"), fromArgs.OpenSimProgram);
        Assert.Equal(Path.GetFullPath("rel-maria"), fromArgs.MariaDbBin);

        var fromEnv = LauncherPaths.Resolve([], env, OperatingSystem.IsWindows());
        Assert.Equal(Path.GetFullPath("rel-env-data"), fromEnv.Data);
        Assert.Equal(Path.GetFullPath("rel-env-opensim"), fromEnv.OpenSimProgram);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void EnsureCreated_makes_the_data_dir_owner_only_on_linux()
    {
        // The generated INIs under <data> carry the database passwords, ConsolePass and the MachineID.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var data = Path.Combine(Path.GetTempPath(), "dg-mode-" + Guid.NewGuid().ToString("N"));
        var existing = Path.Combine(Path.GetTempPath(), "dg-mode-" + Guid.NewGuid().ToString("N"));
        try
        {
            new LauncherPaths(data, "/o", null).EnsureCreated();
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(data));

            Directory.CreateDirectory(existing);
            File.SetUnixFileMode(existing, (UnixFileMode)0b111_101_101); // 0755, as an earlier version left it
            new LauncherPaths(existing, "/o", null).EnsureCreated();
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(existing));
        }
        finally
        {
            foreach (var d in new[] { data, existing })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }
}
