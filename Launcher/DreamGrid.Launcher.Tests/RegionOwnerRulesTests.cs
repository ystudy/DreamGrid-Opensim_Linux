using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Processes;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionOwnerRulesTests
{
    private static readonly Guid Me = Guid.Parse("06000000-0000-0000-0000-000000000006");
    private static readonly Guid Other = Guid.Parse("06000000-0000-0000-0000-000000000007");
    private static readonly Guid Home = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid Beach = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static ComponentStatus Process(ProcessState state) => new("x", state, null, null, null, null);

    /// <summary>Home and Beach share one program; Home is in <paramref name="state"/>.</summary>
    private static (GridStatus Status, RegionStatus Region) Shared(RegionState state, ProcessState robust = ProcessState.Running)
    {
        var home = new RegionStatus(Home, "Home", "Home", state, true, 1000, 1000, 256, ["Beach"]);
        var beach = new RegionStatus(Beach, "Beach", "Home", RegionState.Running, true, 1001, 1000, 256, ["Home"]);
        var status = new GridStatus(Process(ProcessState.Running), Process(robust), [new ProgramStatus("Home", Process(ProcessState.Running), [Home, Beach])],
            [home, beach], true, "http://127.0.0.1:8002");
        return (status, home);
    }

    private static (GridStatus Status, RegionStatus Region) Alone(RegionState state, ProcessState robust = ProcessState.Running)
    {
        var home = new RegionStatus(Home, "Home", "Home", state, true, 1000, 1000, 256, []);
        var status = new GridStatus(Process(ProcessState.Running), Process(robust), [new ProgramStatus("Home", Process(ProcessState.Running), [Home])],
            [home], true, "http://127.0.0.1:8002");
        return (status, home);
    }

    private static Guid? Mine(Guid _) => Me;

    private static RestartOffer Offer((GridStatus Status, RegionStatus Region) s, Func<Guid, Guid?>? ownerOf = null, DateTimeOffset? last = null) =>
        RegionOwnerRules.Restart(s.Status, s.Region, Me, ownerOf ?? Mine, last, Now);

    [Theory]
    [InlineData(RegionState.Running)]
    [InlineData(RegionState.Starting)]
    [InlineData(RegionState.Stopping)]
    public void Running_region_offers_nothing(RegionState state)
    {
        Assert.False(Offer(Alone(state)).Shown);
    }

    [Fact]
    public void Failed_region_offers_restart()
    {
        var offer = Offer(Alone(RegionState.Failed));

        Assert.Equal(new RestartOffer(true, true, null, RegionPower.Restart), offer);
    }

    [Fact]
    public void Stopped_offers_start_only_while_robust_runs()
    {
        var running = Offer(Alone(RegionState.Stopped));
        var down = Offer(Alone(RegionState.Stopped, ProcessState.Stopped));

        Assert.Equal(new RestartOffer(true, true, null, RegionPower.Start), running);
        Assert.False(down.Shown);
    }

    [Fact]
    public void Parked_offers_nothing()
    {
        Assert.False(Offer(Alone(RegionState.Parked)).Shown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_with_unowned_region_not_allowed(bool someoneElses)
    {
        var offer = Offer(Shared(RegionState.Failed), id => id == Home ? Me : someoneElses ? Other : null);

        Assert.True(offer.Shown);
        Assert.False(offer.Allowed);
        Assert.Equal("This region runs with regions that aren't yours. To restart it, contact the grid's staff.", offer.Reason);
        Assert.Equal(RegionPower.Restart, offer.Action);
    }

    [Fact]
    public void Shared_with_own_regions_allowed()
    {
        var offer = Offer(Shared(RegionState.Failed), Mine);

        Assert.Equal(new RestartOffer(true, true, null, RegionPower.Restart), offer);
    }

    [Fact]
    public void Within_ten_minutes_not_allowed_with_time()
    {
        var offer = Offer(Alone(RegionState.Failed), last: Now - TimeSpan.FromMinutes(4));

        Assert.True(offer.Shown);
        Assert.False(offer.Allowed);
        Assert.Equal("This region was restarted a few minutes ago. Try again after 12:06 (UTC).", offer.Reason);
    }

    [Fact]
    public void Ten_minutes_later_allowed_again()
    {
        var offer = Offer(Alone(RegionState.Failed), last: Now - TimeSpan.FromMinutes(10));

        Assert.True(offer.Allowed);
    }
}
