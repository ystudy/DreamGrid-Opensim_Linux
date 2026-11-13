using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Dashboard;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;
using Xunit;
using static GridGate.Launcher.Tests.SplashPictureApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>The Home dashboard's tiles, counts and Who's online (design 2026-11-05-dashboard).</summary>
public sealed class DashboardLayoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly WorldCounts Full = new(
        Residents: 1280, JoinedThisMonth: 6, NeverSignedIn: 9, SignedInToday: 11, SignedInThisMonth: 42,
        LandSquareMetres: 1_310_720, MapRegions: 20,
        Objects: 12480, LinkedObjects: 3100, Parcels: 37, ParcelsForSale: 3, Estates: 2,
        Groups: 14, HypergridLinks: 5, Profiles: 61, Picks: 61, Classifieds: 8);

    private static async Task<IHtmlDocument> HomeAsync(LauncherFactory f, string role = "owner")
    {
        var html = await (await StaffAsync(f, role)).GetStringAsync("/", Ct);
        return new HtmlParser().ParseDocument(html);
    }

    private static string Count(IHtmlDocument page, string key) => page.QuerySelector($"[data-n='{key}']")!.TextContent.Trim();

    [Fact]
    public async Task Tiles_and_counts_show_the_numbers_with_thousands_separators()
    {
        await using var f = Factory();
        f.Counts.Result = Full;
        var page = await HomeAsync(f);

        Assert.Equal("1,280", Count(page, "residents"));
        Assert.Equal("12,480", Count(page, "objects"));
        Assert.Equal("1.3", page.QuerySelector("[data-km2='landSquareMetres']")!.TextContent.Trim());
        Assert.Equal("8", Count(page, "classifieds"));
        Assert.NotNull(page.QuerySelector(".tile[data-tile='signups']"));
        Assert.NotNull(page.QuerySelector("#checked-at"));
    }

    [Fact]
    public async Task A_number_that_could_not_be_counted_shows_a_dash()
    {
        await using var f = Factory();
        f.Counts.Result = WorldCounts.Unknown;
        var page = await HomeAsync(f);

        Assert.Equal("—", Count(page, "residents"));
        Assert.Equal("—", Count(page, "picks"));
        Assert.Null(page.QuerySelector(".alert-danger"));
    }

    [Fact]
    public async Task A_region_server_shows_no_resident_land_or_profile_numbers()
    {
        await using var f = Factory(settings: s => s with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006),
            Hypergrid = false,
        });
        f.Counts.Result = Full;
        var page = await HomeAsync(f);

        Assert.Null(page.QuerySelector(".tile[data-tile='residents']"));
        Assert.Null(page.QuerySelector(".tile[data-tile='land']"));
        Assert.Null(page.QuerySelector(".tile[data-tile='signups']"));
        Assert.Null(page.QuerySelector("[data-count='picks']"));
        Assert.NotNull(page.QuerySelector("[data-count='objects']"));
    }

    [Fact]
    public async Task A_grid_asset_server_says_its_objects_are_this_computers()
    {
        await using var f = Factory(settings: s => s with { Plan = OwnerPlan.GridAsset });
        var page = await HomeAsync(f);

        Assert.Contains("on this computer", page.QuerySelector("[data-count='objects']")!.TextContent);
        Assert.DoesNotContain("on this computer", page.QuerySelector("[data-count='groups']")!.TextContent);
    }

    [Fact]
    public async Task Whos_online_links_each_resident_to_their_page()
    {
        await using var f = Factory();
        var person = Guid.NewGuid();
        f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("Ada Lovelace", Guid.NewGuid(), person)], true);
        var page = await HomeAsync(f);

        Assert.Equal("Ada Lovelace", page.QuerySelector($"#online a[href='/people/{person}']")!.TextContent.Trim());
        Assert.Equal("1", page.QuerySelector("#tile-online")!.TextContent.Trim());
    }

    [Fact]
    public async Task Controls_are_in_the_heading_row_and_the_panels_in_three_columns()
    {
        await using var f = Factory();
        f.Counts.Result = Full;
        var page = await HomeAsync(f);

        Assert.Equal("page-wide", page.Body!.ClassName);
        var controls = page.QuerySelector(".page-head > form#controls");
        Assert.NotNull(controls);
        Assert.NotNull(controls.QuerySelector("details.power-menu button[value=stop]"));
        Assert.NotNull(controls.QuerySelector("button[value=restart-database]"));

        var cols = page.QuerySelectorAll(".home-columns > .home-col");
        Assert.Equal(3, cols.Length);
        Assert.NotNull(cols[0].QuerySelector("#components"));
        Assert.NotNull(cols[0].QuerySelector("#login-uri"));
        Assert.NotNull(cols[1].QuerySelector("#region-rows"));
        Assert.NotNull(cols[1].QuerySelector("#online"));
        Assert.NotNull(cols[2].QuerySelector("#world-counts"));
    }
}
