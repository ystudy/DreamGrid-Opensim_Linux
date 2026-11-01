using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionsPageTests : IAsyncLifetime
{
    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        Programs = TestSettings.ProgramFor(new RegionSettings("Welcome", "Welcome", Guid.NewGuid())),
        Regions = TestSettings.RegionsFor(new RegionSettings("Welcome", "Welcome", Guid.NewGuid())),
    })
    {
        Before = TestAuth.Apply,
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    private async Task<AngleSharp.Html.Dom.IHtmlDocument> PageAsync()
    {
        using var client = await SignedInAsync();
        return new HtmlParser().ParseDocument(await client.GetStringAsync("/regions", Ct));
    }

    [Fact]
    public async Task Nav_has_regions_between_home_and_advanced()
    {
        var doc = await PageAsync();

        Assert.Equal(["Home", "Regions", "Advanced", "Logs", "People", "Sessions", "Emails", "Two-factor"],
            doc.QuerySelectorAll("header nav a").Select(a => a.TextContent.Trim()));
    }

    [Fact]
    public async Task Page_lists_built_in_types_in_order()
    {
        var doc = await PageAsync();

        var names = doc.QuerySelectorAll("#region-types li").Select(li => li.QuerySelector(".type-name")!.TextContent.Trim());
        Assert.Equal(["Default Island", "Sandy Beach", "Forest", "Meadow"], names);
        Assert.Equal(["island", "beach", "forest", "meadow"], doc.QuerySelectorAll("#region-types li").Select(li => li.GetAttribute("data-type")));
    }

    [Fact]
    public async Task Regions_page_lists_custom_types_after_built_in()
    {
        _f.Store.Save(_f.Store.LoadOrCreate() with { CustomTypes = [new CustomRegionType("c-0000abcd", "My Castle", "castle.oar")] });

        var doc = await PageAsync();

        var items = doc.QuerySelectorAll("#region-types li");
        Assert.Equal(["Default Island", "Sandy Beach", "Forest", "Meadow", "My Castle"], items.Select(li => li.QuerySelector(".type-name")!.TextContent.Trim()));
        var castle = items[4];
        Assert.Equal("c-0000abcd", castle.GetAttribute("data-type"));
        Assert.Equal("/img/custom-type.svg", castle.QuerySelector("img")!.GetAttribute("src"));
    }

    [Fact]
    public async Task Custom_type_has_no_picture_endpoint()
    {
        using var client = await SignedInAsync();
        using var response = await client.GetAsync("/regions/types/c-0000abcd.png", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Page_says_map_needs_javascript()
    {
        var doc = await PageAsync();

        Assert.Contains("The map needs JavaScript. You can still add a region by its map position below.",
            doc.QuerySelector("noscript")!.TextContent);
    }

    [Fact]
    public async Task Region_list_links_each_region()
    {
        var id = _f.Store.LoadOrCreate().Regions[0].Uuid;
        var doc = await PageAsync();

        var link = doc.QuerySelector($"#region-list a[href='/regions/region/{id}']");
        Assert.Equal("Welcome", link!.TextContent.Trim());
        Assert.Contains("Runs on its own", doc.QuerySelector("#region-list")!.TextContent);
    }

    [Fact]
    public async Task Region_list_never_says_program()
    {
        var text = (await PageAsync()).Body!.TextContent;

        Assert.DoesNotContain("program", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instance", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Selected_region_row_is_highlighted()
    {
        var id = _f.Store.LoadOrCreate().Regions[0].Uuid;
        using var client = await SignedInAsync();
        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions?region={id}", Ct));

        Assert.Equal(id.ToString(), doc.QuerySelector("#map")!.GetAttribute("data-selected"));
        Assert.NotNull(doc.QuerySelector("#region-list tr.table-active"));
    }

    [Fact]
    public async Task Type_picture_is_404_without_file()
    {
        using var client = await SignedInAsync();
        using var missing = await client.GetAsync("/regions/types/beach.png", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var dir = Path.Combine(StarterContent.ProgramRoot(_f.Paths), "content", "region-types");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "beach.png"), [0x89, 0x50, 0x4E, 0x47], Ct);

        using var found = await client.GetAsync("/regions/types/beach.png", Ct);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal("image/png", found.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Type_picture_refuses_unknown_id()
    {
        using var client = await SignedInAsync();
        using var resp = await client.GetAsync("/regions/types/..%2Fsettings.png", Ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
