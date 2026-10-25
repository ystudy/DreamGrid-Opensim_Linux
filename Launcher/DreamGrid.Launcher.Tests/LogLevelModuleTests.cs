using System.Xml.Linq;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.Runtime;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LogLevelModuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-loglevel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static LauncherSettings Settings(Dictionary<string, Dictionary<string, string>> modules) => new()
    {
        Db = new DatabaseSecrets("r", "a", "b"),
        Console = new ConsoleSecrets("u", "p"),
        MachineId = "m",
        Modules = modules,
    };

    private static string RootLevel(string file) =>
        XDocument.Load(file).Root!.Element("root")!.Element("level")!.Attribute("value")!.Value;

    [Fact]
    public void LogLevelModule_sets_level_in_generated_log_config()
    {
        var s = Settings(new() { ["loglevel"] = new() { ["level"] = "DEBUG" } });
        var paths = new LauncherPaths(_root, Path.Combine(_root, "program"), null);

        new RuntimeStager(paths).WriteLogConfigs("Welcome", LogLevelModule.LevelFor(s));

        Assert.Equal("DEBUG", RootLevel(Path.Combine(paths.RuntimeOpenSim, "dreamgrid-robust.log4net.xml")));
        Assert.Equal("DEBUG", RootLevel(Path.Combine(paths.RuntimeOpenSim, "dreamgrid-region-Welcome.log4net.xml")));
    }

    [Fact]
    public void LogLevelModule_defaults_to_INFO_and_ignores_unknown_levels()
    {
        Assert.Equal("INFO", LogLevelModule.LevelFor(Settings(new())));
        Assert.Equal("INFO", LogLevelModule.LevelFor(Settings(new() { ["loglevel"] = new() { ["level"] = "LOUD" } })));
        Assert.Equal("WARN", LogLevelModule.LevelFor(Settings(new() { ["loglevel"] = new() { ["level"] = "warn" } })));
    }

    [Fact]
    public void LogLevelModule_identity_and_no_ini_keys()
    {
        var m = new LogLevelModule();
        Assert.Equal("loglevel", m.Id);
        Assert.Equal("INFO", m.Defaults["level"]);
        Assert.Empty(m.Contribute(Settings(new())));
        Assert.Empty(((IFeatureModule)m).Commands);
    }
}
