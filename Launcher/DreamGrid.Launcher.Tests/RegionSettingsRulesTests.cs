using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class RegionSettingsRulesTests
{
    private static LauncherSettings Settings(string physics = "ubODE", bool scripts = true) => new()
    {
        Db = new DatabaseSecrets("a", "b", "c"),
        Console = new ConsoleSecrets("u", "p"),
        MachineId = "m",
        DefaultPhysics = physics,
        DefaultScripts = scripts,
    };

    private static ProgramSettings Program(string? physics = null, ProgramOptions? o = null) => new("A", 8004, 8011, physics, o);

    [Fact]
    public void Options_out_of_range_name_the_field()
    {
        var r = RegionSettingsRules.Options(new RegionOptions(MaxAgents: 0, MaxPhysicalSize: 4096));

        Assert.Equal("Use a number from 1 to 500, or leave it empty for OpenSim's default.", r.Errors["MaxAgents"]);
        Assert.Contains("1 to 2,048", r.Errors["MaxPhysicalSize"]);
        Assert.Equal(2, r.Errors.Count);
        Assert.True(RegionSettingsRules.Options(new RegionOptions(500, 1_000_000, 65536, 2048)).Ok);
    }

    [Fact]
    public void Program_rejects_unknown_physics_and_bad_cores()
    {
        Assert.True(RegionSettingsRules.Program("Hybrid", new ProgramOptions(), 8).Errors.ContainsKey("Physics"));
        Assert.True(RegionSettingsRules.Program(null, new ProgramOptions(Cores: [0, 0]), 8).Errors.ContainsKey("Cores"));
        Assert.True(RegionSettingsRules.Program(null, new ProgramOptions(Cores: [8]), 8).Errors.ContainsKey("Cores"));
        Assert.Equal(2, RegionSettingsRules.Program(null, new ProgramOptions(MinTimerInterval: 0.01, FramesPerSecond: 60), 8).Errors.Count);
        Assert.True(RegionSettingsRules.Program("BulletSimThreaded", new ProgramOptions(MinTimerInterval: 10, FramesPerSecond: 5, Cores: [0, 7]), 8).Ok);
    }

    [Fact]
    public void Differences_lists_each_changed_setting_in_tab_order()
    {
        var from = Program(null, new ProgramOptions(MinTimerInterval: 0.5, FramesPerSecond: 11));
        var to = Program("BulletSim", new ProgramOptions(false, 0.2, 20, MapQuality.Better, AllowGods: false, Cores: [0, 1, 2, 3], Priority: CpuPriority.High));

        var lines = RegionSettingsRules.Differences(Settings(), from, to);

        Assert.Equal(
        [
            "Physics will change from ubODE to BulletSim.",
            "Scripts will be turned off.",
            "The script timer rate will change from 0.5 to 0.2 seconds.",
            "The frame rate will change from 11 to 20 per second.",
            "The map will be drawn Better instead of Simple.",
            "God permissions will change.",
            "It will use cores 0–3 instead of every core.",
            "Its priority will change from Normal to High.",
        ], lines);
    }

    [Fact]
    public void Differences_compare_effective_values()
    {
        Assert.Empty(RegionSettingsRules.Differences(Settings(), Program(null), Program("ubODE")));
        Assert.Empty(RegionSettingsRules.Differences(Settings(scripts: false), Program(), Program(o: new ProgramOptions(Scripts: false))));
        Assert.Single(RegionSettingsRules.Differences(Settings("BulletSim"), Program(null), Program("ubODE")));
    }

    [Fact]
    public void Cores_beyond_this_computer_are_ignored()
    {
        Assert.Equal([2], RegionSettingsRules.UsableCores([2, 9, 15], 8));
        Assert.Empty(RegionSettingsRules.UsableCores([9], 8));
        Assert.Empty(RegionSettingsRules.UsableCores(null, 8));
    }

    [Fact]
    public void High_priority_unavailable_on_linux_without_root()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess, "Linux without root only");
        Assert.Equal("On Linux only the root user can raise a program's priority.", RegionSettingsRules.PriorityUnavailable(CpuPriority.High));
        Assert.Null(RegionSettingsRules.PriorityUnavailable(CpuPriority.Normal));
    }
}
