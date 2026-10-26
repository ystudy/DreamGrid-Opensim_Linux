using System.Xml.Linq;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Runtime;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RuntimeStagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-stager-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public RuntimeStagerTests()
    {
        var program = Path.Combine(_root, "program");
        Directory.CreateDirectory(Path.Combine(program, "addin-db-002"));
        Directory.CreateDirectory(Path.Combine(program, "config-include"));
        File.WriteAllBytes(Path.Combine(program, "OpenSim.dll"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(program, "Gloebit.dll"), [4]);
        File.WriteAllText(Path.Combine(program, "addin-db-002", "x"), "x");
        File.WriteAllText(Path.Combine(program, "config-include", "a.ini"), "[A]");
        File.WriteAllText(Path.Combine(program, "x.log"), "log");
        _paths = new LauncherPaths(Path.Combine(_root, "data"), program, null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void First_stage_copies_and_skips_excluded_items()
    {
        Assert.True(new RuntimeStager(_paths).StageIfNeeded());

        var rt = _paths.RuntimeOpenSim;
        Assert.True(File.Exists(Path.Combine(rt, "OpenSim.dll")));
        Assert.True(File.Exists(Path.Combine(rt, "config-include", "a.ini")));
        Assert.False(File.Exists(Path.Combine(rt, "Gloebit.dll")));
        Assert.False(Directory.Exists(Path.Combine(rt, "addin-db-002")));
        Assert.False(File.Exists(Path.Combine(rt, "x.log")));
    }

    [Fact]
    public void Second_stage_with_same_program_is_a_no_op()
    {
        var stager = new RuntimeStager(_paths);
        stager.StageIfNeeded();
        var stray = Path.Combine(_paths.RuntimeOpenSim, "stray.txt");
        File.WriteAllText(stray, "keep");

        Assert.False(stager.StageIfNeeded());
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public void Changed_program_restages()
    {
        var stager = new RuntimeStager(_paths);
        stager.StageIfNeeded();
        var stray = Path.Combine(_paths.RuntimeOpenSim, "stray.txt");
        File.WriteAllText(stray, "gone");

        File.SetLastWriteTimeUtc(Path.Combine(_paths.OpenSimProgram, "OpenSim.dll"), DateTime.UtcNow.AddHours(1));

        Assert.True(stager.StageIfNeeded());
        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(Path.Combine(_paths.RuntimeOpenSim, "OpenSim.dll")));
    }

    /// <summary>
    /// Only the program's own live bin/OpenSim.ini is left out; the region prototype
    /// config-include/Proto/Robust/OpenSim.ini must be staged (GridConfigWriter reads it; found in Task 10).
    /// </summary>
    [Fact]
    public void Only_the_top_level_OpenSim_ini_is_excluded()
    {
        var proto = Path.Combine(_paths.OpenSimProgram, "config-include", "Proto", "Robust");
        Directory.CreateDirectory(proto);
        File.WriteAllText(Path.Combine(proto, "OpenSim.ini"), "[Const]");
        File.WriteAllText(Path.Combine(_paths.OpenSimProgram, "OpenSim.ini"), "[Live]");

        new RuntimeStager(_paths).StageIfNeeded();

        Assert.True(File.Exists(Path.Combine(_paths.RuntimeOpenSim, "config-include", "Proto", "Robust", "OpenSim.ini")));
        Assert.False(File.Exists(Path.Combine(_paths.RuntimeOpenSim, "OpenSim.ini")));
    }

    [Fact]
    public void Log_configs_use_absolute_forward_slash_paths_under_Logs()
    {
        new RuntimeStager(_paths).WriteLogConfigs("Region1", "DEBUG");

        var robust = XDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "dreamgrid-robust.log4net.xml"));
        Assert.Equal(Path.Combine(_paths.Logs, "robust.log").Replace('\\', '/'),
            robust.Descendants("file").Single().Attribute("value")!.Value);
        Assert.Equal("DEBUG", robust.Root!.Element("root")!.Element("level")!.Attribute("value")!.Value);

        var region = XDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "dreamgrid-region-Region1.log4net.xml"));
        Assert.Equal(Path.Combine(_paths.Logs, "region-Region1.log").Replace('\\', '/'),
            region.Descendants("file").Single().Attribute("value")!.Value);
    }

    [Theory]
    [InlineData("Robust.dll")]
    [InlineData("config-include/a.ini")]
    [InlineData("Robust.HG.ini.proto")]
    public void A_changed_Robust_dll_include_or_prototype_restages(string rel)
    {
        File.WriteAllBytes(Path.Combine(_paths.OpenSimProgram, "Robust.dll"), [7]);
        File.WriteAllText(Path.Combine(_paths.OpenSimProgram, "Robust.HG.ini.proto"), "[Const]");
        var stager = new RuntimeStager(_paths);
        stager.StageIfNeeded();
        Assert.False(stager.NeedsStaging());

        File.SetLastWriteTimeUtc(Path.Combine(_paths.OpenSimProgram, rel), DateTime.UtcNow.AddHours(1));

        Assert.True(stager.NeedsStaging());
        Assert.True(stager.StageIfNeeded());
        Assert.False(stager.NeedsStaging());
    }
}
