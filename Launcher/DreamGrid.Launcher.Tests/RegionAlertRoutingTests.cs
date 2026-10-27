using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionAlertRoutingTests
{
    private static readonly RegionSettings Home = new("Home", "Home", Guid.NewGuid());
    private static readonly RegionSettings Beach = new("Home", "Beach", Guid.NewGuid(), 1001);
    private static readonly IReadOnlyList<RegionSettings> Regions = [Home, Beach];

    private static RegionAlert Logins(string login, string name, Guid id) => new("region_ready", login, name, id, null);

    [Fact]
    public void Alert_for_unknown_region_is_ignored()
    {
        var logins = new RegionLogins();
        logins.Apply(Logins("enabled", "Home", Home.Uuid), Regions);

        Assert.False(logins.Apply(Logins("disabled", "Elsewhere", Guid.NewGuid()), Regions));
        Assert.True(logins.Enabled(Home.Uuid));
        Assert.False(logins.Enabled(Beach.Uuid));
    }

    [Fact]
    public void Alert_matches_by_id_then_name()
    {
        var logins = new RegionLogins();

        Assert.True(logins.Apply(Logins("enabled", "Renamed by hand", Beach.Uuid), Regions));
        Assert.True(logins.Enabled(Beach.Uuid));
        Assert.True(logins.Apply(Logins("enabled", "HOME", Guid.Empty), Regions));
        Assert.True(logins.Enabled(Home.Uuid));
    }

    [Fact]
    public void A_newer_alert_wins_over_an_older_query_answer()
    {
        var logins = new RegionLogins();
        var seen = logins.Version(Home.Uuid);
        logins.Apply(Logins("enabled", "Home", Home.Uuid), Regions);

        logins.SetIfUnchanged(Home.Uuid, seen, enabled: false);

        Assert.True(logins.Enabled(Home.Uuid));
    }

    private static ComponentStatus Program(ProcessState state) => new("program-Home", state, null, null, null, null);

    [Fact]
    public void Adopted_program_reports_unloaded_region_as_stopped()
    {
        var running = Program(ProcessState.Running);

        Assert.Equal(RegionState.Stopped, RegionStates.Derive(Beach, running, held: false, loaded: [Home.Uuid], loginsEnabled: false));
        Assert.Equal(RegionState.Running, RegionStates.Derive(Home, running, held: false, loaded: [Home.Uuid], loginsEnabled: true));
        Assert.Equal(RegionState.Starting, RegionStates.Derive(Home, running, held: false, loaded: null, loginsEnabled: false));
    }

    [Fact]
    public void Region_state_follows_parked_held_and_program_state()
    {
        Assert.Equal(RegionState.Parked, RegionStates.Derive(Home with { Parked = true }, Program(ProcessState.Running), false, null, true));
        Assert.Equal(RegionState.Stopped, RegionStates.Derive(Home, Program(ProcessState.Running), held: true, null, true));
        Assert.Equal(RegionState.Failed, RegionStates.Derive(Home, Program(ProcessState.Crashed), false, null, false));
        Assert.Equal(RegionState.Starting, RegionStates.Derive(Home, Program(ProcessState.Starting), false, null, false));
        Assert.Equal(RegionState.Stopped, RegionStates.Derive(Home, null, false, null, false));
    }
}
