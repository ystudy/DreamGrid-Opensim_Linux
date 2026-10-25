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
        Assert.Equal("/arg/data", fromArgs.Data);
        Assert.Equal("/arg/opensim", fromArgs.OpenSimProgram);
        Assert.Equal("/arg/maria", fromArgs.MariaDbBin);

        var fromEnv = LauncherPaths.Resolve([], env, isWindows: false);
        Assert.Equal("/env/data", fromEnv.Data);
        Assert.Equal("/env/opensim", fromEnv.OpenSimProgram);
        Assert.Equal("/env/maria", fromEnv.MariaDbBin);

        var defaults = LauncherPaths.Resolve([], new Dictionary<string, string?> { ["DREAMGRID_OPENSIM"] = "/o" }, isWindows: false);
        Assert.Equal("/var/lib/dreamgrid", defaults.Data);
        Assert.Null(defaults.MariaDbBin);
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
        Assert.Equal("/var/lib/dreamgrid", LauncherPaths.Resolve([], env, isWindows: false).Data);
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
}
