using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class PlanCatalogTests
{
    private static readonly OwnerPlan[] Order =
        [OwnerPlan.Local, OwnerPlan.SimplePublic, OwnerPlan.GridAsset, OwnerPlan.RegionServer, OwnerPlan.Connected];

    // Rows are "from", columns are "to", in card order.
    private static readonly bool[][] Matrix =
    [
        [false, true, true, false, false],
        [true, false, true, false, false],
        [true, true, false, false, false],
        [false, false, false, false, false],
        [false, false, false, false, false],
    ];

    public static TheoryData<OwnerPlan, OwnerPlan, bool> Pairs()
    {
        var d = new TheoryData<OwnerPlan, OwnerPlan, bool>();
        for (var i = 0; i < 5; i++)
            for (var j = 0; j < 5; j++)
                d.Add(Order[i], Order[j], Matrix[i][j]);
        return d;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Matrix_matches_the_spec(OwnerPlan from, OwnerPlan to, bool allowed)
    {
        var (ok, reason) = PlanCatalog.CanChange(from, to);
        Assert.Equal(allowed, ok);
        if (!ok) Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Ids_are_unique_and_round_trip()
    {
        Assert.Equal(Order, PlanCatalog.All.Select(p => p.Plan));
        Assert.Equal(5, PlanCatalog.All.Select(p => p.Id).Distinct().Count());
        foreach (var p in Order) Assert.Equal(p, PlanCatalog.ById(PlanCatalog.Get(p).Id)!.Plan);
        Assert.Null(PlanCatalog.ById("nope"));
        Assert.Null(PlanCatalog.ById(null));
        Assert.Equal("connected-server", PlanCatalog.Get(OwnerPlan.Connected).Id);
    }

    [Fact]
    public void Only_single_machine_plans_are_available_in_6a()
    {
        Assert.Equal([true, true, true, false, false], PlanCatalog.All.Select(p => p.Available));
    }

    [Fact]
    public void Hypergrid_capability_per_plan()
    {
        Assert.Equal(HypergridCapability.None, PlanCatalog.Get(OwnerPlan.Local).Hypergrid);
        Assert.Equal(HypergridCapability.Switch, PlanCatalog.Get(OwnerPlan.SimplePublic).Hypergrid);
        Assert.Equal(HypergridCapability.Switch, PlanCatalog.Get(OwnerPlan.GridAsset).Hypergrid);
        Assert.Equal(HypergridCapability.FollowsGridAsset, PlanCatalog.Get(OwnerPlan.RegionServer).Hypergrid);
        Assert.Equal(HypergridCapability.OtherGrid, PlanCatalog.Get(OwnerPlan.Connected).Hypergrid);
    }

    private static LauncherSettings S(OwnerPlan? plan, string host, bool hypergrid) => new()
    {
        Plan = plan,
        Hostname = host,
        Hypergrid = hypergrid,
        Db = new DatabaseSecrets("rootpw", "GENROBUSTPW", "GENOPENSIMPW"),
        Console = new ConsoleSecrets("conuser", "conpass32chars"),
        MachineId = "machine-1",
    };

    [Fact]
    public void HypergridOn_needs_switch_capable_plan_and_public_address()
    {
        Assert.False(PlanCatalog.HypergridOn(S(OwnerPlan.GridAsset, "192.168.1.20", true)));
        Assert.True(PlanCatalog.HypergridOn(S(OwnerPlan.GridAsset, "203.0.113.7", true)));
        Assert.False(PlanCatalog.HypergridOn(S(OwnerPlan.Local, "203.0.113.7", true)));
        Assert.False(PlanCatalog.HypergridOn(S(null, "203.0.113.7", true)));
        Assert.False(PlanCatalog.HypergridOn(S(OwnerPlan.SimplePublic, "203.0.113.7", false)));
    }

    [Fact]
    public void Every_plan_has_advisories_and_runs()
    {
        foreach (var p in PlanCatalog.All)
        {
            Assert.NotEmpty(p.Advisories);
            Assert.NotEmpty(p.Runs);
            Assert.NotEmpty(p.YouNeed);
            Assert.False(string.IsNullOrWhiteSpace(p.Summary));
            Assert.False(string.IsNullOrWhiteSpace(p.HypergridLine));
        }
        Assert.Contains(PlanCatalog.Get(OwnerPlan.GridAsset).Advisories,
            a => a.Contains("You can run regions here too, but it isn't advised: busy regions slow down logins, inventory and the map for everyone on the grid."));
    }
}
