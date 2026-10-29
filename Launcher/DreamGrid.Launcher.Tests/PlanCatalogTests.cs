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

    [Theory]
    [InlineData(OwnerPlan.Local, true)]
    [InlineData(OwnerPlan.SimplePublic, true)]
    [InlineData(OwnerPlan.GridAsset, true)]
    [InlineData(OwnerPlan.RegionServer, false)]
    [InlineData(OwnerPlan.Connected, false)]
    [InlineData(null, true)]
    public void RunsRobust(OwnerPlan? plan, bool expected) =>
        Assert.Equal(expected, PlanCatalog.RunsRobust(S(plan, "192.168.1.20", false)));

    [Fact]
    public void HypergridOn_region_server_follows_join()
    {
        var s = S(OwnerPlan.RegionServer, "192.168.1.20", true);
        var join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "7.1.0", true, 8004, 8006);

        Assert.False(PlanCatalog.HypergridOn(s));
        Assert.True(PlanCatalog.HypergridOn(s with { Join = join }));
        Assert.False(PlanCatalog.HypergridOn((s with { Join = join with { Hypergrid = false } })));
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

    [Fact]
    public void Grid_asset_advisories_split_by_address_kind()
    {
        var info = PlanCatalog.Get(OwnerPlan.GridAsset);
        var lan = PlanCatalog.AdvisoriesFor(info, publicAddress: false);
        var pub = PlanCatalog.AdvisoriesFor(info, publicAddress: true);

        Assert.Contains(lan, a => a.Contains("only computers on this network can visit"));
        Assert.DoesNotContain(lan, a => a.Contains("passwords cross the internet"));
        Assert.Contains(pub, a => a.Contains("passwords cross the internet") && a.StartsWith("Your router must forward"));
        Assert.DoesNotContain(pub, a => a.Contains("only computers on this network can visit"));
        Assert.Contains(lan, a => a.StartsWith("If this computer is off"));
        Assert.Contains(pub, a => a.StartsWith("If this computer is off"));
        Assert.Equal(PlanCatalog.Get(OwnerPlan.Local).Advisories, PlanCatalog.AdvisoriesFor(PlanCatalog.Get(OwnerPlan.Local), true));
    }

    private const string NotPublic =
        "That's an address only computers on your network can reach. Enter your public IP address or your grid's name.";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("192.168.1.20")]
    [InlineData("localhost")]
    public void Simple_public_needs_a_public_address(string? hostname) =>
        Assert.Equal(NotPublic, PlanCatalog.AddressError(OwnerPlan.SimplePublic, hostname));

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("world.example.com")]
    public void Local_refuses_a_public_address(string hostname)
    {
        var error = PlanCatalog.AddressError(OwnerPlan.Local, hostname);
        Assert.NotNull(error);
        Assert.Contains("Simple Public Server", error);
    }

    [Theory]
    [InlineData(OwnerPlan.Local, null)]
    [InlineData(OwnerPlan.Local, "192.168.1.20")]
    [InlineData(OwnerPlan.SimplePublic, "203.0.113.7")]
    [InlineData(OwnerPlan.SimplePublic, "world.example.com")]
    [InlineData(OwnerPlan.GridAsset, null)]
    [InlineData(OwnerPlan.GridAsset, "192.168.1.20")]
    [InlineData(OwnerPlan.GridAsset, "203.0.113.7")]
    [InlineData(OwnerPlan.RegionServer, "192.168.1.20")]
    [InlineData(OwnerPlan.Connected, "203.0.113.7")]
    [InlineData(null, "203.0.113.7")]
    [InlineData(null, null)]
    public void Address_accepted(OwnerPlan? plan, string? hostname) => Assert.Null(PlanCatalog.AddressError(plan, hostname));

    [Theory]
    [InlineData(OwnerPlan.GridAsset)]
    [InlineData(null)]
    public void Other_plans_refuse_what_the_hostname_check_refuses(OwnerPlan? plan) =>
        Assert.Equal(SettingsValidator.HostnameError("my host"), PlanCatalog.AddressError(plan, "my host"));

    [Fact]
    public void Advisories_name_no_fixed_port()
    {
        foreach (var p in PlanCatalog.All)
            Assert.DoesNotContain(p.Advisories, a => a.Contains("8002"));
    }
}
