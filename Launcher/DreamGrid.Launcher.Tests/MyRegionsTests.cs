using System.Globalization;
using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>A guest's My regions page and the restart for a stuck region.</summary>
public sealed class MyRegionsTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid SunsetId = Guid.Parse("50000000-0000-0000-0000-000000000003");
    private static readonly Guid RockyId = Guid.Parse("50000000-0000-0000-0000-000000000004");
    private static readonly Guid Stranger = Guid.Parse("06000000-0000-0000-0000-0000000000aa");

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        // Home and Beach share one program; Sunset Shore and Rocky Cove have their own.
        Programs =
        [
            new ProgramSettings("Home", 8004, 8011, "ubODE"),
            new ProgramSettings("Sunset", 8005, 8012, "ubODE"),
            new ProgramSettings("Rocky", 8006, 8013, "ubODE"),
        ],
        Regions =
        [
            new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
            new RegionSettings("Home", "Beach", BeachId, X: 1001, UdpPort: 8004),
            new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1002, UdpPort: 8005),
            new RegionSettings("Rocky", "Rocky Cove", RockyId, X: 1003, UdpPort: 8006),
        ],
    })
    {
        Before = TestAuth.Apply,
    };

    public ValueTask InitializeAsync()
    {
        TestAuth.AddStaff(_f.Robust);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RegionRights Rights => _f.Services.GetRequiredService<RegionRights>();

    private static ComponentStatus Process(ProcessState state, TimeSpan? uptime = null) => new("x", state, null, uptime, null, null);

    /// <summary>The grid as the page sees it: Home and Beach running, Sunset Shore in <paramref name="sunset"/>, Rocky Cove running.</summary>
    private void SetStatus(RegionState sunset, RegionState home = RegionState.Running, ProcessState robust = ProcessState.Running)
    {
        RegionStatus Region(Guid id, string name, string instance, RegionState state, int x, params string[] with) =>
            new(id, name, instance, state, true, x, 1000, 256, with);
        _f.Grid.Status = new GridStatus(Process(ProcessState.Running), Process(robust),
            [
                new ProgramStatus("Home", Process(ProcessState.Running, TimeSpan.FromHours(2)), [HomeId, BeachId]),
                new ProgramStatus("Sunset", Process(sunset == RegionState.Running ? ProcessState.Running : ProcessState.Stopped), [SunsetId]),
                new ProgramStatus("Rocky", Process(ProcessState.Running, TimeSpan.FromMinutes(30)), [RockyId]),
            ],
            [
                Region(HomeId, "Home", "Home", home, 1000, "Beach"),
                Region(BeachId, "Beach", "Home", RegionState.Running, 1001, "Home"),
                Region(SunsetId, "Sunset Shore", "Sunset", sunset, 1002),
                Region(RockyId, "Rocky Cove", "Rocky", RegionState.Running, 1003),
            ],
            true, "http://127.0.0.1:8002");
    }

    private HttpClient Client() => _f.CreateClient(new() { AllowAutoRedirect = false });

    private async Task<HttpClient> GuestAsync()
    {
        var client = Client();
        using var r = await client.GetAsync("/__test/guest-signin", Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static async Task<IHtmlDocument> DocAsync(HttpClient client, string url) =>
        new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));

    private static string Text(IHtmlDocument doc) => doc.Body!.TextContent;

    /// <summary>Posts the restart form for <paramref name="id"/> with a token from the guest's home page.</summary>
    private static async Task<HttpResponseMessage> RestartAsync(HttpClient client, Guid id)
    {
        var token = (await DocAsync(client, "/guest")).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await client.PostAsync("/guest/regions?handler=restart",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = id.ToString(), ["__RequestVerificationToken"] = token }), Ct);
    }

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

    private static void AssertAudit(JsonElement entry, string target, string result)
    {
        Assert.Equal(("Test Guest", TestAuth.GuestId.ToString(), "guest", target, result),
            (entry.GetProperty("actor").GetString(), entry.GetProperty("actorId").GetString(), entry.GetProperty("role").GetString(),
                entry.GetProperty("target").GetString(), entry.GetProperty("result").GetString()));
    }

    [Fact]
    public async Task Owner_sees_only_their_regions()
    {
        SetStatus(RegionState.Failed);
        Rights.Grant(HomeId, TestAuth.GuestId, "Test Guest");
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        Rights.Grant(RockyId, Stranger, "Some One");
        _f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("A One", HomeId), new OnlineAvatar("B Two", HomeId), new OnlineAvatar("C Three", RockyId)], true);
        using var client = await GuestAsync();

        var home = await DocAsync(client, "/guest");
        var page = await DocAsync(client, "/guest/regions");

        Assert.Equal("/guest/regions", home.QuerySelectorAll("a").Single(a => a.TextContent == "My regions").GetAttribute("href"));
        var cards = page.QuerySelectorAll("section.card").ToList();
        Assert.Equal(["Home", "Sunset Shore"], cards.Select(c => c.QuerySelector("h2")!.TextContent.Trim()));
        var text = Text(page);
        Assert.DoesNotContain("Rocky Cove", text);
        Assert.DoesNotContain("Beach", text);
        Assert.Contains("Normal restarts are done in-world from the viewer's Region/Estate window.", text);

        var homeCard = cards[0].TextContent;
        Assert.Contains("This region is running.", homeCard);
        Assert.Contains("People here now: 2", homeCard);
        Assert.Contains("Last started " + (_f.Time.GetUtcNow() - TimeSpan.FromHours(2)).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (UTC)", homeCard);
        Assert.Null(cards[0].QuerySelector("button"));

        var sunsetCard = cards[1];
        Assert.Contains("This region is not responding.", sunsetCard.TextContent);
        Assert.Contains("People here now: 0", sunsetCard.TextContent);
        Assert.Contains("not running", sunsetCard.TextContent);
        var button = sunsetCard.QuerySelector("button[type=submit]")!;
        Assert.Equal("Restart this region", button.TextContent.Trim());
        Assert.False(button.HasAttribute("disabled"));
    }

    [Fact]
    public async Task Guest_without_regions_has_no_link_and_empty_page()
    {
        SetStatus(RegionState.Failed);
        Rights.Grant(RockyId, Stranger, "Some One");
        using var client = await GuestAsync();

        var home = await DocAsync(client, "/guest");
        var page = await DocAsync(client, "/guest/regions");

        Assert.DoesNotContain(home.QuerySelectorAll("a"), a => a.GetAttribute("href") == "/guest/regions");
        Assert.Contains("You don't have any regions.", Text(page));
        Assert.Empty(page.QuerySelectorAll("section.card"));
    }

    [Fact]
    public async Task Restart_runs_and_is_audited()
    {
        SetStatus(RegionState.Failed);
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        using var client = await GuestAsync();

        using var r = await RestartAsync(client, SunsetId);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest/regions", r.Headers.Location?.OriginalString);
        Assert.True(await WaitForCallAsync($"RegionPower Restart {SunsetId}"));
        AssertAudit(Assert.Single(Audit("region-restart")), "Sunset Shore", "ok");
        Assert.Contains("Restarting Sunset Shore. It can take a minute or two.", Text(await DocAsync(client, "/guest/regions")));
    }

    [Fact]
    public async Task Stopped_region_is_started()
    {
        SetStatus(RegionState.Stopped);
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        using var client = await GuestAsync();

        var page = await DocAsync(client, "/guest/regions");
        using var r = await RestartAsync(client, SunsetId);

        Assert.Equal("Start this region", page.QuerySelector("button[type=submit]")!.TextContent.Trim());
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.True(await WaitForCallAsync($"RegionPower Start {SunsetId}"));
    }

    [Fact]
    public async Task Second_restart_within_ten_minutes_refused()
    {
        SetStatus(RegionState.Failed);
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        using var client = await GuestAsync();
        (await RestartAsync(client, SunsetId)).Dispose();
        Assert.True(await WaitForCallAsync($"RegionPower Restart {SunsetId}"));
        var tryAgain = (_f.Time.GetUtcNow() + TimeSpan.FromMinutes(10)).UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        _f.Time.Advance(TimeSpan.FromMinutes(3));

        using var second = await RestartAsync(client, SunsetId);

        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        var page = await DocAsync(client, "/guest/regions");
        Assert.Contains($"This region was restarted a few minutes ago. Try again after {tryAgain} (UTC).", Text(page));
        Assert.True(page.QuerySelector("button[type=submit]")!.HasAttribute("disabled"));
        Assert.Single(_f.Grid.Snapshot(), c => c.StartsWith("RegionPower"));
        Assert.Equal(["ok", "refused"], Audit("region-restart").Select(e => e.GetProperty("result").GetString()));

        // Ten minutes after the first it works again.
        _f.Time.Advance(TimeSpan.FromMinutes(7));
        using var third = await RestartAsync(client, SunsetId);
        Assert.Equal(HttpStatusCode.Redirect, third.StatusCode);
        Assert.Equal(["ok", "refused", "ok"], Audit("region-restart").Select(e => e.GetProperty("result").GetString()));
    }

    [Fact]
    public async Task Crafted_post_for_someone_elses_region_refused()
    {
        SetStatus(RegionState.Failed);
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        Rights.Grant(RockyId, Stranger, "Some One");
        using var client = await GuestAsync();

        using var r = await RestartAsync(client, RockyId);
        using var nobodys = await RestartAsync(client, BeachId);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nobodys.StatusCode);
        var entries = Audit("region-restart");
        Assert.Equal(2, entries.Count);
        AssertAudit(entries[0], "Rocky Cove", "refused");
        AssertAudit(entries[1], "Beach", "refused");
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("RegionPower"));
    }

    [Fact]
    public async Task Right_being_taken_away_cannot_restart()
    {
        SetStatus(RegionState.Failed);
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        Rights.SetHomeEstate(SunsetId, 100);
        Rights.Remove(SunsetId);
        using var client = await GuestAsync();

        var page = await DocAsync(client, "/guest/regions");
        using var r = await RestartAsync(client, SunsetId);

        Assert.Empty(page.QuerySelectorAll("section.card"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        AssertAudit(Assert.Single(Audit("region-restart")), "Sunset Shore", "refused");
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("RegionPower"));
    }

    [Fact]
    public async Task Region_sharing_a_program_with_others_is_not_restarted()
    {
        SetStatus(RegionState.Running, home: RegionState.Failed);
        Rights.Grant(HomeId, TestAuth.GuestId, "Test Guest");
        using var client = await GuestAsync();

        var page = await DocAsync(client, "/guest/regions");
        using var r = await RestartAsync(client, HomeId);

        Assert.Contains("This region runs with regions that aren't yours. To restart it, contact the grid's staff.", Text(page));
        Assert.True(page.QuerySelector("button[type=submit]")!.HasAttribute("disabled"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        AssertAudit(Assert.Single(Audit("region-restart")), "Home", "refused");
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("RegionPower"));
    }

    [Fact]
    public async Task Online_unavailable_shows_unknown()
    {
        SetStatus(RegionState.Failed);
        Rights.Grant(SunsetId, TestAuth.GuestId, "Test Guest");
        _f.OnlineUsers.Result = OnlineList.Unavailable;
        using var client = await GuestAsync();

        var page = await DocAsync(client, "/guest/regions");

        Assert.Contains("People here now: unknown", Text(page));
    }

    [Fact]
    public async Task Staff_cookie_cannot_open_my_regions()
    {
        SetStatus(RegionState.Failed);
        foreach (var role in new[] { "owner", "admin", "supervisor" })
        {
            using var client = Client();
            using var signIn = await client.GetAsync("/__test/signin?role=" + role, Ct);

            using var r = await client.GetAsync("/guest/regions", Ct);

            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/guest/sign-in", r.Headers.Location is { IsAbsoluteUri: true } u ? u.AbsolutePath : r.Headers.Location?.OriginalString.Split('?')[0]);
        }
    }
}
