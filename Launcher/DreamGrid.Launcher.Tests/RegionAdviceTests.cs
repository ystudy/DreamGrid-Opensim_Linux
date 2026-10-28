using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionAdviceTests
{
    private const long MB = 1L << 20;

    private static ComponentStatus Process(string name, ProcessState state, long? memory) =>
        new(name, state, null, null, memory, null);

    private static GridStatus StatusWith(params ProgramStatus[] programs) =>
        new(Process("Database", ProcessState.Running, null), Process("Robust", ProcessState.Running, null), programs, [], true, "http://localhost:8002");

    private static readonly GridStatus NoPrograms = StatusWith();

    [Fact]
    public void Own_with_plenty_of_memory() => Assert.Equal(
        "Uses about 400 MB more memory. This computer has 5.2 GB free, enough for about 13 more regions run this way. If it crashes, only this region goes offline.",
        RegionAdvice.OnItsOwn(NoPrograms, 5_583_457_484, 1).Text);

    [Fact]
    public void Own_when_memory_is_short()
    {
        var advice = RegionAdvice.OnItsOwn(NoPrograms, 500 * MB, 1);
        Assert.True(advice.Short);
        Assert.Equal(1, advice.MoreRegions);
        Assert.EndsWith("This computer may not have enough free memory for another region run on its own. Sharing saves memory.", advice.Text);
    }

    [Fact]
    public void Own_with_unknown_free_memory_drops_the_clause()
    {
        var advice = RegionAdvice.OnItsOwn(NoPrograms, null, 1);
        Assert.False(advice.Short);
        Assert.Null(advice.MoreRegions);
        Assert.Equal("Uses about 400 MB more memory. If it crashes, only this region goes offline.", advice.Text);
    }

    [Fact]
    public void Estimate_for_bigger_sizes_adds_150_MB_per_extra_square()
    {
        Assert.Equal(400 * MB, RegionAdvice.OnItsOwn(NoPrograms, null, 1).EstimateBytes);
        Assert.Equal(850 * MB, RegionAdvice.OnItsOwn(NoPrograms, null, 2).EstimateBytes);
    }

    [Fact]
    public void Estimate_follows_running_programs()
    {
        var status = StatusWith(
            new ProgramStatus("Home", Process("Home", ProcessState.Running, 600 * MB), []),
            new ProgramStatus("Beach", Process("Beach", ProcessState.Running, 800 * MB), []),
            new ProgramStatus("Idle", Process("Idle", ProcessState.Stopped, 5000 * MB), []),
            new ProgramStatus("Blank", Process("Blank", ProcessState.Running, null), []));
        Assert.Equal(700 * MB, RegionAdvice.OnItsOwn(status, null, 1).EstimateBytes);
        // scaled the same way: 700 * (400 + 150 * 3) / 400
        Assert.Equal(700 * MB * 850 / 400, RegionAdvice.OnItsOwn(status, null, 2).EstimateBytes);
    }

    [Fact]
    public void Share_names_the_group() => Assert.Equal(
        "Saves memory, but these regions start, stop, restart and crash together. Restarting Sunset Shore also restarts Home and Beach, and drops everyone in them.",
        RegionAdvice.Share(["Home", "Beach"], "Sunset Shore"));

    [Fact]
    public void Size_risk_for_4() => Assert.Equal(
        "A 4×4 region is 16 squares of land in one region. It uses several times the memory of a 1×1 and can feel slower to visitors. Many viewers handle very large regions poorly.",
        RegionAdvice.SizeRisk(4));

    [Fact]
    public void Size_risk_for_2() => Assert.Equal(
        "A 2×2 region is 4 squares of land. It uses more memory than a 1×1.", RegionAdvice.SizeRisk(2));

    [Fact]
    public void Size_risk_for_1_is_null() => Assert.Null(RegionAdvice.SizeRisk(1));

    [Fact]
    public void Placement_connected() => Assert.Equal("This region will join Beach on its east side.",
        RegionAdvice.Placement(new(true, null, [new("Beach", "east")])));

    [Fact]
    public void Placement_two_neighbours() => Assert.Equal("This region will join Beach on its east side and Home on its north side.",
        RegionAdvice.Placement(new(true, null, [new("Beach", "east"), new("Home", "north")])));

    [Fact]
    public void Placement_island() => Assert.Equal("This will be a separate island; visitors reach it by teleport.",
        RegionAdvice.Placement(new(true, null, [])));

    [Fact]
    public void Placement_taken() => Assert.Equal("That spot is taken by Beach.",
        RegionAdvice.Placement(new(false, "Beach", [])));

    [Fact]
    public void Group_label() => Assert.Equal("Home, Beach (2 regions)", RegionAdvice.GroupLabel(["Home", "Beach"]));

    [Fact]
    public void Group_label_for_one_region() => Assert.Equal("Home", RegionAdvice.GroupLabel(["Home"]));
}
