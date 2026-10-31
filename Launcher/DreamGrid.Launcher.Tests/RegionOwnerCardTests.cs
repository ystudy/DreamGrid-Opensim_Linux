using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The "Region owner" card on a region's page, and where region rights show: the regions list, People and erasing a region.</summary>
public sealed class RegionOwnerCardTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid SunsetId = Guid.Parse("40000000-0000-0000-0000-000000000003");
    private static readonly Guid OldId = Guid.Parse("40000000-0000-0000-0000-000000000005");

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        // Home and Beach share one program; Sunset Shore has its own; Old Town is in Recently deleted.
        Programs = [new ProgramSettings("Home", 8004, 8011, "ubODE"), new ProgramSettings("Sunset", 8005, 8012, "ubODE")],
        Regions =
        [
            new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
            new RegionSettings("Home", "Beach", BeachId, X: 1001, UdpPort: 8004),
            new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1002, UdpPort: 8005),
            new RegionSettings("Old", "Old Town", OldId, X: 1500, UdpPort: 8006, DeletedAt: new DateTimeOffset(2026, 10, 20, 14, 30, 0, TimeSpan.Zero)),
        ],
    })
    {
        Before = TestAuth.Apply,
    };

    public ValueTask InitializeAsync()
    {
        TestAuth.AddStaff(_f.Robust);
        _f.Robust.Add("Sam", "Suspended", -1, "pass-word1");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SignedInAsync(string role = "admin")
    {
        var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private RegionRights Rights => _f.Services.GetRequiredService<RegionRights>();

    private static async Task<IHtmlDocument> DocAsync(HttpClient client, string url) =>
        new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string handler, params (string, string)[] fields)
    {
        var token = (await DocAsync(client, $"/regions/region/{id}")).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = fields.ToDictionary(f => f.Item1, f => f.Item2);
        form["__RequestVerificationToken"] = token;
        return await client.PostAsync($"/regions/region/{id}?handler={handler}", new FormUrlEncodedContent(form), Ct);
    }

    private static Task<HttpResponseMessage> GrantAsync(HttpClient client, Guid id, string name) => PostAsync(client, id, "grant", ("owner", name));

    private static string Text(IHtmlDocument doc) => doc.Body!.TextContent;

    private List<JsonElement> Audit(string action)
    {
        var path = Path.Combine(_f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(e => e.GetProperty("action").GetString() == action).ToList();
    }

    private async Task<bool> WaitForCallAsync(string call)
    {
        for (var i = 0; i < 100; i++)
        {
            if (_f.Grid.Snapshot().Contains(call)) return true;
            await Task.Delay(50, Ct);
        }
        return false;
    }

    [Fact]
    public async Task Admin_grants_by_name()
    {
        using var client = await SignedInAsync();
        var plain = await DocAsync(client, $"/regions/region/{SunsetId}");
        Assert.Contains("Region owner", Text(plain));
        Assert.Equal("Give region rights", plain.QuerySelector("#region-owner button[type=submit]")!.TextContent.Trim());
        Assert.Null(plain.QuerySelector("#region-owner form[data-confirm]"));

        // The name is cleaned like a sign-in name: stray spaces don't matter.
        using var granted = await GrantAsync(client, SunsetId, "  Test   Guest ");

        Assert.Equal(HttpStatusCode.Redirect, granted.StatusCode);
        Assert.Equal($"/regions/region/{SunsetId}", granted.Headers.Location?.OriginalString);
        var right = Rights.Of(SunsetId)!;
        Assert.Equal((TestAuth.GuestId, "Test Guest", EstateMove.ToOwner, _f.Time.GetUtcNow()), (right.Owner, right.OwnerName, right.Pending, right.Since));

        var text = Text(await DocAsync(client, $"/regions/region/{SunsetId}"));
        Assert.Contains("Test Guest has region rights for Sunset Shore.", text);
        Assert.Contains($"Test Guest has region rights since {_f.Time.GetUtcNow().UtcDateTime:d MMMM yyyy}.", text);
        Assert.Contains("Moves to Test Guest's estate when Sunset Shore next runs.", text);
        var entry = Assert.Single(Audit("region-rights-granted"));
        Assert.Equal(("Test Admin", "admin", "Sunset Shore → Test Guest", "ok"),
            (entry.GetProperty("actor").GetString(), entry.GetProperty("role").GetString(), entry.GetProperty("target").GetString(), entry.GetProperty("result").GetString()));
    }

    [Fact]
    public async Task Granting_again_to_the_same_person_changes_nothing()
    {
        using var client = await SignedInAsync();
        (await GrantAsync(client, SunsetId, "Test Guest")).Dispose();
        Rights.MoveDone(SunsetId);

        using var again = await GrantAsync(client, SunsetId, "Test Guest");

        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Null(Rights.Of(SunsetId)!.Pending);
        Assert.Contains("Test Guest already has region rights for Sunset Shore.", Text(await DocAsync(client, $"/regions/region/{SunsetId}")));
        Assert.Single(Audit("region-rights-granted"));
    }

    [Fact]
    public async Task Granting_to_someone_else_replaces_the_owner()
    {
        _f.Robust.Add("Other", "Guest", 0, "pass-word1");
        using var client = await SignedInAsync();
        (await GrantAsync(client, SunsetId, "Test Guest")).Dispose();
        Rights.MoveDone(SunsetId);
        // The card offers the form only while nobody holds the region, so a handover is posted by hand here.

        using var handed = await GrantAsync(client, SunsetId, "Other Guest");

        Assert.Equal(HttpStatusCode.Redirect, handed.StatusCode);
        var right = Rights.Of(SunsetId)!;
        Assert.Equal(("Other Guest", EstateMove.ToOwner), (right.OwnerName, right.Pending));
        Assert.Empty(Rights.OwnedBy(TestAuth.GuestId));
    }

    [Fact]
    public async Task Unknown_name_shows_error()
    {
        using var client = await SignedInAsync();

        using var response = await GrantAsync(client, SunsetId, "Nobody Here");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("There's no avatar called Nobody Here on this grid.", doc.QuerySelector("#region-owner .error-box")!.TextContent.Trim());
        // What was typed stays in the field.
        Assert.Equal("Nobody Here", doc.QuerySelector("#region-owner input[name=owner]")!.GetAttribute("value"));
        Assert.Null(Rights.Of(SunsetId));
        Assert.Empty(Audit("region-rights-granted"));
    }

    [Fact]
    public async Task Incomplete_name_asks_for_both_names()
    {
        using var client = await SignedInAsync();

        using var response = await GrantAsync(client, SunsetId, "Test");

        Assert.Contains("Type the avatar's first and last name.", Text(new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(Ct))));
        Assert.Null(Rights.Of(SunsetId));
    }

    [Fact]
    public async Task Suspended_avatar_refused()
    {
        using var client = await SignedInAsync();

        using var response = await GrantAsync(client, SunsetId, "Sam Suspended");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Sam Suspended is suspended.", new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(Ct)).QuerySelector("#region-owner .error-box")!.TextContent.Trim());
        Assert.Null(Rights.Of(SunsetId));
        Assert.Empty(Audit("region-rights-granted"));
    }

    [Fact]
    public async Task Robust_down_is_said_in_plain_words()
    {
        using var client = await SignedInAsync();
        _f.Robust.Down = true;

        using var response = await GrantAsync(client, SunsetId, "Test Guest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("The grid's account service isn't running. Start your world first.",
            new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(Ct)).QuerySelector("#region-owner .error-box")!.TextContent.Trim());
        Assert.Null(Rights.Of(SunsetId));
    }

    [Fact]
    public async Task Granting_on_a_deleted_region_is_not_found()
    {
        using var client = await SignedInAsync();
        var token = (await DocAsync(client, $"/regions/region/{SunsetId}")).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var response = await client.PostAsync($"/regions/region/{OldId}?handler=grant",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["owner"] = "Test Guest", ["__RequestVerificationToken"] = token }), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(Rights.Of(OldId));
    }

    [Fact]
    public async Task Supervisor_cannot_post_grant()
    {
        using var admin = await SignedInAsync();
        var token = (await DocAsync(admin, $"/regions/region/{SunsetId}")).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        using var supervisor = await SignedInAsync("supervisor");
        var form = new Dictionary<string, string> { ["owner"] = "Test Guest", ["__RequestVerificationToken"] = token };

        using var grant = await supervisor.PostAsync($"/regions/region/{SunsetId}?handler=grant", new FormUrlEncodedContent(form), Ct);
        using var revoke = await supervisor.PostAsync($"/regions/region/{SunsetId}?handler=revoke", new FormUrlEncodedContent(form), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        Assert.Null(Rights.Of(SunsetId));
    }

    [Fact]
    public async Task Remove_is_audited()
    {
        using var client = await SignedInAsync();
        (await GrantAsync(client, SunsetId, "Test Guest")).Dispose();
        var card = await DocAsync(client, $"/regions/region/{SunsetId}");
        // The remove button asks first (confirm.js reads data-confirm), and the page loads the script.
        var form = card.QuerySelector("#region-owner form[data-confirm]")!;
        Assert.Equal("Remove Test Guest's region rights for Sunset Shore?", form.GetAttribute("data-confirm"));
        Assert.Equal("Remove region rights", form.QuerySelector("button")!.TextContent.Trim());
        Assert.NotNull(card.QuerySelector("script[src='/js/confirm.js']"));
        Assert.Null(card.QuerySelector("#region-owner input[name=owner]"));

        using var removed = await PostAsync(client, SunsetId, "revoke");

        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        var entry = Assert.Single(Audit("region-rights-removed"));
        Assert.Equal(("Test Admin", "admin", "Sunset Shore → Test Guest", "ok"),
            (entry.GetProperty("actor").GetString(), entry.GetProperty("role").GetString(), entry.GetProperty("target").GetString(), entry.GetProperty("result").GetString()));
        // It never moved, so there is nothing to move back: the right is gone and the form is back.
        Assert.Null(Rights.Of(SunsetId));
        var after = await DocAsync(client, $"/regions/region/{SunsetId}");
        Assert.Contains("Test Guest no longer has region rights for Sunset Shore.", Text(after));
        Assert.NotNull(after.QuerySelector("#region-owner input[name=owner]"));
    }

    [Fact]
    public async Task Remove_after_the_move_says_it_goes_back()
    {
        using var client = await SignedInAsync();
        (await GrantAsync(client, SunsetId, "Test Guest")).Dispose();
        Rights.SetHomeEstate(SunsetId, 1);
        Rights.MoveDone(SunsetId);

        (await PostAsync(client, SunsetId, "revoke")).Dispose();

        Assert.Equal(EstateMove.ToGrid, Rights.Of(SunsetId)!.Pending);
        var text = Text(await DocAsync(client, $"/regions/region/{SunsetId}"));
        Assert.Contains("Moves back to the grid's estate when Sunset Shore next runs.", text);
        Assert.DoesNotContain("has region rights since", text);
        Assert.Empty(Rights.OwnedBy(TestAuth.GuestId));
        // Removing again changes nothing and is not audited a second time.
        (await PostAsync(client, SunsetId, "revoke")).Dispose();
        Assert.Single(Audit("region-rights-removed"));
    }

    [Fact]
    public async Task Shared_program_note_and_own_program_button()
    {
        using var client = await SignedInAsync();

        var shared = await DocAsync(client, $"/regions/region/{HomeId}");
        var alone = await DocAsync(client, $"/regions/region/{SunsetId}");

        Assert.Contains("Home shares its program with Beach. Its owner can restart it from the website only when every region in that program is theirs.", Text(shared));
        var button = shared.QuerySelector("#region-owner form[action$='handler=ownprogram']")!;
        Assert.Equal("Give Home its own program", button.QuerySelector("button")!.TextContent.Trim());
        // Beach runs in the program, so leaving it restarts Beach and the page asks first.
        Assert.Contains("Beach", button.GetAttribute("data-confirm"));
        Assert.DoesNotContain("shares its program", Text(alone));
        Assert.Null(alone.QuerySelector("form[action$='handler=ownprogram']"));

        using var posted = await PostAsync(client, HomeId, "ownprogram");

        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        // The same action as the Runs page.
        Assert.True(await WaitForCallAsync($"ChangeRunsWith {HomeId} own"));
        Assert.Contains("Changing how Home runs.", Text(await DocAsync(client, $"/regions/region/{HomeId}")));
    }

    [Fact]
    public async Task Own_program_for_a_region_on_its_own_does_nothing()
    {
        using var client = await SignedInAsync();

        using var posted = await PostAsync(client, SunsetId, "ownprogram");

        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        await Task.Delay(100, Ct);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("ChangeRunsWith"));
    }

    [Fact]
    public async Task Regions_list_shows_owner()
    {
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        using var client = await SignedInAsync();

        var doc = await DocAsync(client, "/regions");

        var rows = doc.QuerySelectorAll("#region-list tbody tr").ToDictionary(tr => tr.GetAttribute("data-region")!);
        Assert.Equal("Owner: Test Guest", rows[SunsetId.ToString()].QuerySelector(".region-owner-mark")!.TextContent.Trim());
        Assert.Null(rows[HomeId.ToString()].QuerySelector(".region-owner-mark"));
        Assert.DoesNotContain("Owner:", rows[BeachId.ToString()].TextContent);
    }

    [Fact]
    public async Task People_find_shows_regions()
    {
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        Rights.Grant(HomeId, TestAuth.GuestId, "Test Guest");
        Rights.Grant(OldId, TestAuth.GuestId, "Test Guest"); // in Recently deleted: not listed
        Rights.Grant(BeachId, TestAuth.GuestId, "Test Guest");
        Rights.Remove(BeachId); // never moved, so gone at once
        using var client = await SignedInAsync();

        var guest = await DocAsync(client, "/people?name=Test+Guest");
        var admin = await DocAsync(client, "/people?name=Test+Admin");

        Assert.Equal("Region rights: Home, Sunset Shore", guest.QuerySelector("#person-regions")!.TextContent.Trim());
        Assert.Equal("Region rights: none", admin.QuerySelector("#person-regions")!.TextContent.Trim());
    }

    [Fact]
    public async Task Erase_forgets_rights()
    {
        Rights.Grant(OldId, TestAuth.GuestId, "Test Guest");
        Rights.SetHomeEstate(OldId, 1);
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        using var client = await SignedInAsync();
        var token = (await DocAsync(client, "/regions/deleted")).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var erased = await client.PostAsync("/regions/deleted?handler=erase", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = OldId.ToString(), ["confirmed"] = "true", ["__RequestVerificationToken"] = token,
        }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, erased.StatusCode);
        Assert.True(await WaitForCallAsync($"EraseRegion {OldId}"));
        Assert.Null(Rights.Of(OldId));
        Assert.Null(Rights.HomeEstateOf(OldId));
        // Other regions keep theirs.
        Assert.NotNull(Rights.Of(SunsetId));
    }

    [Fact]
    public async Task Erase_that_asks_first_keeps_rights()
    {
        Rights.Grant(OldId, TestAuth.GuestId, "Test Guest");
        using var client = await SignedInAsync();
        var token = (await DocAsync(client, "/regions/deleted")).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var asked = await client.PostAsync("/regions/deleted?handler=erase", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = OldId.ToString(), ["__RequestVerificationToken"] = token,
        }), Ct);

        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.NotNull(Rights.Of(OldId));
    }
}
