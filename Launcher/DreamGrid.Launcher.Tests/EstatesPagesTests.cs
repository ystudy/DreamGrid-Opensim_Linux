using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Estates;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DreamGrid.Launcher.Tests.SplashPictureApiTests;

namespace DreamGrid.Launcher.Tests;

/// <summary>The admin's Estates pages at /estates and /estates/estate (spec 2026-11-05-hypergrid-links-estates-design, 2.10).</summary>
public sealed class EstatesPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();
    private static readonly Guid Home = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Beach = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Owned = Guid.Parse("11111111-0000-0000-0000-000000000003");
    private static readonly Guid Ann = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("22222222-0000-0000-0000-000000000002");

    /// <summary>
    /// Avalon with three regions: Home in the grid's estate, Beach in "Spare", and Owned, which Ann owns, in "Ann Guest Estate".
    /// Region consoles answer by changing the fake tables.
    /// </summary>
    private sealed record Rig(LauncherFactory F, int Grid, int Spare, int AnnEstate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    private static Rig Make()
    {
        var f = Factory(settings: s => s with
        {
            Regions =
            [
                new RegionSettings("Home", "Home", Home, 1000, 1000),
                new RegionSettings("Beach", "Beach", Beach, 1001, 1000),
                new RegionSettings("Owned", "Owned", Owned, 1002, 1000),
            ],
        });
        f.Robust.Add("Ann", "Guest", 0, "pass-word1", Ann);
        f.Robust.Add("Bob", "Builder", 0, "pass-word1", Bob);
        var grid = f.GridEstates.Add("Avalon Estate", TestAuth.OwnerId, Home);
        var spare = f.GridEstates.Add("Spare", TestAuth.OwnerId, Beach);
        var ann = f.GridEstates.Add("Ann Guest Estate", Ann, Owned);
        f.Grid.OnLinkEstate = (region, estate) => f.GridEstates.Link(region, estate);
        f.Grid.OnCreateEstate = (_, owner, name) => (f.GridEstates.Add(name, owner), name);
        var rights = f.Services.GetRequiredService<RegionRights>();
        rights.Grant(Owned, Ann, "Ann Guest");
        rights.SetEstate(Ann, ann, "Ann Guest Estate");
        rights.MoveDone(Owned);
        return new Rig(f, grid, spare, ann);
    }

    private static string Url(int id) => "/estates/estate?id=" + id;

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string url, Dictionary<string, string> fields, string tokenFrom)
    {
        var page = Parser.ParseDocument(await client.GetStringAsync(tokenFrom, Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static async Task<IHtmlDocument> PostAsync(HttpClient client, int id, string handler, Dictionary<string, string> fields)
    {
        using var r = await PostRawAsync(client, $"{Url(id)}&handler={handler}", fields, Url(id));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string? Error(IHtmlDocument page) => page.QuerySelector(".alert-danger")?.TextContent.Trim();
    private static string? Notice(IHtmlDocument page) => page.QuerySelector(".alert-success")?.TextContent.Trim();

    [Theory]
    [InlineData("supervisor")]
    [InlineData(null)]
    public async Task Supervisors_and_anonymous_cannot_open_or_post(string? role)
    {
        await using var rig = Make();
        var client = role is null ? Client(rig.F) : await StaffAsync(rig.F, role);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/estates", Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync(Url(rig.Spare), Ct)).StatusCode);
        if (role is not null)
        {
            using var r = await PostRawAsync(client, $"{Url(rig.Spare)}&handler=Rename", new() { ["name"] = "Taken Over" }, "/people");
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        }
        Assert.Equal("Spare", rig.F.GridEstates.NameOf(rig.Spare));
    }

    [Fact]
    public async Task Not_there_without_a_guest_side()
    {
        await using var f = Factory(settings: s => s with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006),
        });
        var client = await StaffAsync(f, "owner");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/estates", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_list_shows_every_estate()
    {
        await using var rig = Make();
        var page = await PageAsync(await StaffAsync(rig.F, "admin"), "/estates");
        var text = page.QuerySelector("#estates-list")!.TextContent;
        Assert.Contains("Avalon Estate", text);
        Assert.Contains("Spare", text);
        Assert.Contains("Ann Guest", text);
    }

    [Fact]
    public async Task Rename_owner_and_managers_change_the_tables_and_reload_the_estate()
    {
        await using var rig = Make();
        var client = await StaffAsync(rig.F, "admin");

        Assert.Contains("Renamed to Sandbox", Notice(await PostAsync(client, rig.Spare, "Rename", new() { ["name"] = "  Sandbox " })));
        Assert.Equal("Sandbox", rig.F.GridEstates.NameOf(rig.Spare));

        Assert.Contains("Bob Builder owns", Notice(await PostAsync(client, rig.Spare, "Owner", new() { ["owner"] = "Bob Builder" })));
        Assert.Equal(Bob, rig.F.GridEstates.OwnerOf(rig.Spare));

        Assert.Contains("is a manager", Notice(await PostAsync(client, rig.Spare, "AddManager", new() { ["manager"] = "Ann Guest" })));
        Assert.Equal([Ann], rig.F.GridEstates.ManagersOf(rig.Spare));
        Assert.Contains("already a manager", Error(await PostAsync(client, rig.Spare, "AddManager", new() { ["manager"] = "Ann Guest" })));
        Assert.Contains("owns", Error(await PostAsync(client, rig.Spare, "AddManager", new() { ["manager"] = "Bob Builder" })));

        Assert.Contains("isn't a manager", Notice(await PostAsync(client, rig.Spare, "RemoveManager", new() { ["manager"] = Ann.ToString() })));
        Assert.Empty(rig.F.GridEstates.ManagersOf(rig.Spare));

        Assert.Equal(4, rig.F.Grid.Snapshot().Count(c => c == $"ReloadEstate {rig.Spare}"));
        var audit = AuditText(rig.F);
        foreach (var action in new[] { "estate-renamed", "estate-owner-changed", "estate-manager-added", "estate-manager-removed" })
            Assert.Contains(action, audit);
    }

    [Fact]
    public async Task A_region_program_that_doesnt_answer_is_named()
    {
        await using var rig = Make();
        rig.F.Grid.OnReloadEstate = _ => ["Beach"];
        var page = await PostAsync(await StaffAsync(rig.F, "admin"), rig.Spare, "AddManager", new() { ["manager"] = "Ann Guest" });
        Assert.Contains("Beach didn't answer", Notice(page));
    }

    [Theory]
    [InlineData("", "Type an estate name")]
    [InlineData("Two  spaces", "single spaces")]
    [InlineData("Say \"hi\"", "double quotes")]
    [InlineData("--evil", "--")]
    [InlineData("ann guest estate", "already called")]
    [InlineData("Avalon Estate", "grid's own")]
    public async Task Bad_names_are_refused(string name, string says)
    {
        await using var rig = Make();
        var page = await PostAsync(await StaffAsync(rig.F, "admin"), rig.Spare, "Rename", new() { ["name"] = name });
        Assert.Contains(says, Error(page));
        Assert.Equal("Spare", rig.F.GridEstates.NameOf(rig.Spare));
    }

    [Fact]
    public async Task The_grids_estate_keeps_its_name_and_owner_and_is_never_deleted()
    {
        await using var rig = Make();
        var client = await StaffAsync(rig.F, "owner");
        var page = await PageAsync(client, Url(rig.Grid));
        Assert.NotNull(page.QuerySelector("#name-fixed"));
        Assert.NotNull(page.QuerySelector("#owner-fixed"));
        Assert.Null(page.QuerySelector("#estate-delete"));
        Assert.NotNull(page.QuerySelector("#estate-add-manager"));

        Assert.Contains("keeps its name", Error(await PostAsync(client, rig.Grid, "Rename", new() { ["name"] = "Other" })));
        Assert.Contains("grid owner", Error(await PostAsync(client, rig.Grid, "Owner", new() { ["owner"] = "Bob Builder" })));
        rig.F.GridEstates.Link(Home, rig.Spare);
        Assert.Contains("can't be deleted", Error(await PostAsync(client, rig.Grid, "Delete", new() { ["confirm"] = "true" })));
        Assert.True(rig.F.GridEstates.Exists(rig.Grid));
        Assert.Equal(TestAuth.OwnerId, rig.F.GridEstates.OwnerOf(rig.Grid));
    }

    [Fact]
    public async Task A_region_owners_estate_keeps_its_owner_and_its_regions_stay()
    {
        await using var rig = Make();
        var client = await StaffAsync(rig.F, "admin");
        Assert.Contains("made for Ann Guest", Error(await PostAsync(client, rig.AnnEstate, "Owner", new() { ["owner"] = "Bob Builder" })));
        Assert.Equal(Ann, rig.F.GridEstates.OwnerOf(rig.AnnEstate));

        // Ann's region can't be moved into another estate here.
        var move = await PostAsync(client, rig.Spare, "Move", new() { ["region"] = Owned.ToString() });
        Assert.Contains("belongs to Ann Guest", Error(move));
        Assert.DoesNotContain(rig.F.Grid.Snapshot(), c => c.StartsWith("LinkEstate " + Owned, StringComparison.Ordinal));

        // A rename is remembered for the next region Ann is given.
        Assert.Null(Error(await PostAsync(client, rig.AnnEstate, "Rename", new() { ["name"] = "Ann's Islands" })));
        Assert.Equal("Ann's Islands", rig.F.Services.GetRequiredService<RegionRights>().OwnerEstateOf(Ann)!.Name);
    }

    [Fact]
    public async Task A_region_moves_in_and_an_empty_estate_can_be_deleted()
    {
        await using var rig = Make();
        var client = await StaffAsync(rig.F, "admin");
        var page = await PageAsync(client, Url(rig.Grid));
        Assert.Contains("Beach", page.QuerySelector("#estate-move")!.TextContent);

        Assert.Contains("Moved Beach into Avalon Estate", Notice(await PostAsync(client, rig.Grid, "Move", new() { ["region"] = Beach.ToString() })));
        Assert.Contains($"LinkEstate {Beach} {rig.Grid}", rig.F.Grid.Snapshot());

        var empty = await PostAsync(client, rig.Spare, "Delete", new() { });
        Assert.Contains("Tick the box", Error(empty));
        using var deleted = await PostRawAsync(client, $"{Url(rig.Spare)}&handler=Delete", new() { ["confirm"] = "true" }, Url(rig.Spare));
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Contains(rig.Spare, rig.F.Estates.Removed);
        Assert.Contains("estate-deleted", AuditText(rig.F));
    }

    [Fact]
    public async Task An_estate_with_regions_is_not_deleted()
    {
        await using var rig = Make();
        var page = await PostAsync(await StaffAsync(rig.F, "admin"), rig.Spare, "Delete", new() { ["confirm"] = "true" });
        Assert.Contains("still has 1 region", Error(page));
        Assert.DoesNotContain(rig.Spare, rig.F.Estates.Removed);
    }

    [Fact]
    public async Task A_region_that_isnt_running_is_not_moved()
    {
        await using var rig = Make();
        rig.F.Grid.OnLinkEstate = (_, _) => throw new InvalidOperationException("Start Beach first.");
        var page = await PostAsync(await StaffAsync(rig.F, "admin"), rig.Grid, "Move", new() { ["region"] = Beach.ToString() });
        Assert.Contains("a region moves only while it runs", Error(page));
    }

    [Fact]
    public async Task A_new_estate_is_made_through_a_running_region()
    {
        await using var rig = Make();
        var client = await StaffAsync(rig.F, "admin");
        using var r = await PostRawAsync(client, "/estates?handler=Create", new() { ["name"] = "Mainland", ["owner"] = "Bob Builder" }, "/estates");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains(rig.F.Grid.Snapshot(), c => c.StartsWith("CreateEstate ", StringComparison.Ordinal) && c.EndsWith($"{Bob} Mainland", StringComparison.Ordinal));
        Assert.Contains("estate-created", AuditText(rig.F));

        using var taken = await PostRawAsync(client, "/estates?handler=Create", new() { ["name"] = "mainland", ["owner"] = "Bob Builder" }, "/estates");
        var page = Parser.ParseDocument(await taken.Content.ReadAsStringAsync(Ct));
        Assert.Contains("already called", Error(page));

        using var nobody = await PostRawAsync(client, "/estates?handler=Create", new() { ["name"] = "Other", ["owner"] = "No Such" }, "/estates");
        Assert.Contains("No avatar called", Error(Parser.ParseDocument(await nobody.Content.ReadAsStringAsync(Ct))));
    }

    [Fact]
    public async Task Too_many_managers_are_refused()
    {
        await using var rig = Make();
        var client = await StaffAsync(rig.F, "admin");
        for (var i = 0; i < GridEstates.MaxManagers; i++)
        {
            var id = Guid.NewGuid();
            rig.F.Robust.Add("Helper", "N" + i, 0, "pass-word1", id);
            Assert.Null(Error(await PostAsync(client, rig.Spare, "AddManager", new() { ["manager"] = "Helper N" + i })));
        }
        Assert.Contains("at most", Error(await PostAsync(client, rig.Spare, "AddManager", new() { ["manager"] = "Ann Guest" })));
    }

    [Fact]
    public async Task A_database_that_is_down_is_said_in_plain_words()
    {
        await using var rig = Make();
        rig.F.GridEstates.Down = true;
        var client = await StaffAsync(rig.F, "admin");
        Assert.NotNull((await PageAsync(client, "/estates")).QuerySelector("#estates-unavailable"));
        Assert.NotNull((await PageAsync(client, Url(rig.Spare))).QuerySelector("#estate-unavailable"));
    }
}
