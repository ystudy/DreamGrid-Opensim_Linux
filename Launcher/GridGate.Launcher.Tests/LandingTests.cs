using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Which regions people land on (2.14) and the Robust lines that say so.</summary>
public sealed class LandingTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static LauncherSettings Make(LandingSettings? landing, DateTimeOffset? bDeleted = null) => new()
    {
        Db = new DatabaseSecrets("r", "g", "o"),
        Console = new ConsoleSecrets("u", "p"),
        MachineId = "m",
        Regions =
        [
            new RegionSettings("One", "Welcome Area", A),
            new RegionSettings("One", "Beach", B) { DeletedAt = bDeleted },
            new RegionSettings("Two", "Forest", C),
        ],
        Landing = landing,
    };

    [Fact]
    public void Without_a_choice_the_first_region_is_the_default()
    {
        var s = Make(null);

        Assert.Equal(A, Landing.Default(s)!.Uuid);
        Assert.False(Landing.DefaultChosen(s));
        Assert.Empty(Landing.Fallbacks(s));
    }

    [Fact]
    public void The_chosen_default_and_fallbacks_are_used()
    {
        var s = Make(new LandingSettings(C, [A, B]));

        Assert.Equal(C, Landing.Default(s)!.Uuid);
        Assert.True(Landing.DefaultChosen(s));
        Assert.Equal([A, B], Landing.Fallbacks(s).Select(r => r.Uuid));
    }

    [Fact]
    public void A_deleted_or_unknown_region_is_never_used()
    {
        var s = Make(new LandingSettings(B, [B, Guid.NewGuid(), C]), bDeleted: DateTimeOffset.UnixEpoch);

        Assert.Equal(A, Landing.Default(s)!.Uuid);
        Assert.False(Landing.DefaultChosen(s));
        Assert.Equal([C], Landing.Fallbacks(s).Select(r => r.Uuid));
    }

    [Fact]
    public void The_default_is_not_also_a_fallback()
    {
        var s = Make(new LandingSettings(A, [A, C]));

        Assert.Equal([C], Landing.Fallbacks(s).Select(r => r.Uuid));
    }

    [Fact]
    public void No_regions_means_no_default_and_no_lines()
    {
        var s = Make(null) with { Regions = [] };

        Assert.Null(Landing.Default(s));
        Assert.Empty(Landing.Lines(s));
    }

    /// <summary>
    /// Robust adds flags to a region's row and never clears them, so every other region is written with the flags taken off:
    /// a region dropped from Landing stops being one.
    /// </summary>
    [Fact]
    public void Lines_set_the_landing_flags_and_take_them_off_every_other_region()
    {
        var s = Make(new LandingSettings(C, [A]));

        Assert.Equal(
        [
            $"Region_{A} = \"FallbackRegion, -DefaultRegion, -DefaultHGRegion\"",
            $"Region_{B} = \"-DefaultRegion, -DefaultHGRegion, -FallbackRegion\"",
            $"Region_{C} = \"DefaultRegion, DefaultHGRegion, -FallbackRegion\"",
        ], Landing.Lines(s));
    }

    [Fact]
    public void Lines_skip_deleted_regions()
    {
        var s = Make(null, bDeleted: DateTimeOffset.UnixEpoch);

        Assert.Equal(
        [
            $"Region_{A} = \"DefaultRegion, DefaultHGRegion, -FallbackRegion\"",
            $"Region_{C} = \"-DefaultRegion, -DefaultHGRegion, -FallbackRegion\"",
        ], Landing.Lines(s));
    }
}
