using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Staff sessions on the real host: the session check, the roles' policies and the 5-minute level recheck.</summary>
public sealed class StaffAccessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherFactory Factory()
    {
        var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "admin")
    {
        var client = Client(f);
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static string? LocationPath(HttpResponseMessage r) =>
        r.Headers.Location is { IsAbsoluteUri: true } u ? u.AbsolutePath : r.Headers.Location?.OriginalString.Split('?')[0];

    private static async Task AssertSentToLoginAsync(HttpClient client, string url = "/dashboard")
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login", LocationPath(r));
    }

    private static async Task AssertOkAsync(HttpClient client, string url = "/dashboard")
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static async Task<HttpResponseMessage> PostSessionsAsync(HttpClient client, string handler, string? id = null)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/profile/sessions", Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };
        if (id is not null) form["id"] = id;
        return await client.PostAsync("/profile/sessions?handler=" + handler, new FormUrlEncodedContent(form), Ct);
    }

    [Fact]
    public async Task Cookie_without_session_is_refused()
    {
        await using var f = Factory();
        var old = Client(f);
        (await old.GetAsync("/__test/signin-old", Ct)).Dispose();

        await AssertSentToLoginAsync(old);
        await AssertOkAsync(await SignedInAsync(f));
    }

    [Fact]
    public async Task Sign_out_everywhere_ends_other_browser()
    {
        await using var f = Factory();
        var a = await SignedInAsync(f);
        var b = await SignedInAsync(f);
        await AssertOkAsync(b);

        using var r = await PostSessionsAsync(a, "EndAll");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login", LocationPath(r));
        await AssertSentToLoginAsync(b);
        await AssertSentToLoginAsync(a);
        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "sign-out-everywhere");
        Assert.Equal("Test Admin", entry.GetProperty("actor").GetString());
        Assert.Equal(TestAuth.AdminId.ToString(), entry.GetProperty("actorId").GetString());
    }

    [Fact]
    public async Task Sessions_page_lists_own_sessions_and_ends_one()
    {
        await using var f = Factory();
        var a = await SignedInAsync(f);
        var b = await SignedInAsync(f);
        await SignedInAsync(f, "owner"); // someone else's session is not listed

        var page = new HtmlParser().ParseDocument(await a.GetStringAsync("/profile/sessions", Ct));
        var rows = page.QuerySelectorAll("#sessions tbody tr");
        Assert.Equal(2, rows.Length);
        Assert.Single(rows, row => row.ClassList.Contains("this-session"));

        var sessions = f.Services.GetRequiredService<StaffSessions>();
        var other = rows.Single(row => !row.ClassList.Contains("this-session")).GetAttribute("data-session");
        Assert.Contains(sessions.List(TestAuth.AdminId), s => s.Id == other);

        using var r = await PostSessionsAsync(a, "End", other);

        Assert.Equal("/profile/sessions", LocationPath(r));
        await AssertSentToLoginAsync(b);
        await AssertOkAsync(a);
    }

    [Fact]
    public async Task Sign_out_ends_only_this_session()
    {
        await using var f = Factory();
        var a = await SignedInAsync(f);
        var b = await SignedInAsync(f);
        var page = new HtmlParser().ParseDocument(await a.GetStringAsync("/profile/sessions", Ct));
        var token = page.QuerySelector("form[action='/logout'] input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        (await a.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct)).Dispose();

        Assert.Single(f.Services.GetRequiredService<StaffSessions>().List(TestAuth.AdminId));
        await AssertOkAsync(b);
    }

    [Fact]
    public async Task Supervisor_refused_on_admin_page()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f, "supervisor");

        using var page = await client.GetAsync("/regions", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("You can't open this page", await page.Content.ReadAsStringAsync(Ct));
        using var api = await client.GetAsync("/api/logs?source=launcher", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
        await AssertOkAsync(client, "/profile/sessions");

        // Home leads a supervisor to their own page, not the sign-in page.
        using var home = await client.GetAsync("/", Ct);
        Assert.Equal("/profile/sessions", LocationPath(home));
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string name, string url = "/login")
    {
        var login = new HtmlParser().ParseDocument(await client.GetStringAsync("/login", Ct));
        var form = new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Password"] = "pass-word1",
            ["__RequestVerificationToken"] = login.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!,
        };
        return await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    [Fact]
    public async Task Supervisor_signs_in_and_lands_on_sessions()
    {
        await using var f = Factory();
        var client = Client(f);

        using var r = await LoginAsync(client, "Test Supervisor", "/login?ReturnUrl=%2Flogs");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/profile/sessions", LocationPath(r));
        await AssertOkAsync(client, "/profile/sessions");
        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "sign-in");
        Assert.Equal("supervisor", entry.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Signing_in_again_replaces_this_browsers_session()
    {
        await using var f = Factory();
        var client = Client(f);
        (await LoginAsync(client, "Test Admin")).Dispose();
        var first = Assert.Single(f.Services.GetRequiredService<StaffSessions>().List(TestAuth.AdminId));

        (await LoginAsync(client, "Test Admin")).Dispose();

        var second = Assert.Single(f.Services.GetRequiredService<StaffSessions>().List(TestAuth.AdminId));
        Assert.NotEqual(first.Id, second.Id);
        await AssertOkAsync(client);
    }

    [Fact]
    public async Task Supervisor_post_is_audited_as_supervisor()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f, "supervisor");

        (await PostSessionsAsync(client, "End", "no-such-session")).Dispose();

        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "request");
        Assert.Equal("supervisor", entry.GetProperty("role").GetString());
        Assert.Equal("Test Supervisor", entry.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Recheck_demotes_after_level_drop()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f);
        f.Robust.Add("Test", "Admin", 0, "pass-word1", TestAuth.AdminId);

        // Within 5 minutes the level is not read again.
        f.Time.Advance(TimeSpan.FromMinutes(4));
        await AssertOkAsync(client);
        Assert.DoesNotContain(Audit(f), e => e.GetProperty("action").GetString() == "session-ended");

        f.Time.Advance(TimeSpan.FromMinutes(1));
        await AssertSentToLoginAsync(client);

        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "session-ended");
        Assert.Equal("role changed", entry.GetProperty("target").GetString());
        Assert.Equal(TestAuth.AdminId.ToString(), entry.GetProperty("actorId").GetString());
        Assert.Empty(f.Services.GetRequiredService<StaffSessions>().List(TestAuth.AdminId));
    }

    [Fact]
    public async Task Recheck_keeps_session_when_robust_is_down()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f);
        f.Robust.Down = true;

        f.Time.Advance(TimeSpan.FromMinutes(5));
        await AssertOkAsync(client);
        var lookups = f.Robust.Lookups;

        // After a failure the level is asked again only a minute later, not on every request.
        f.Time.Advance(TimeSpan.FromSeconds(30));
        await AssertOkAsync(client);
        Assert.Equal(lookups, f.Robust.Lookups);
        f.Time.Advance(TimeSpan.FromSeconds(30));
        await AssertOkAsync(client);
        Assert.Equal(lookups + 1, f.Robust.Lookups);

        Assert.DoesNotContain(Audit(f), e => e.GetProperty("action").GetString() == "session-ended");
        Assert.Single(f.Services.GetRequiredService<StaffSessions>().List(TestAuth.AdminId));
    }

    [Fact]
    public async Task Recheck_keeps_session_when_robust_sends_a_failure_reply()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f);
        f.Robust.FailureReply = true;
        var lookups = f.Robust.Lookups;

        f.Time.Advance(TimeSpan.FromMinutes(5));
        await AssertOkAsync(client);

        Assert.Equal(lookups + 1, f.Robust.Lookups); // it was asked
        Assert.DoesNotContain(Audit(f), e => e.GetProperty("action").GetString() == "session-ended");
        Assert.Single(f.Services.GetRequiredService<StaffSessions>().List(TestAuth.AdminId));
    }

    [Fact]
    public async Task Recheck_ends_the_sessions_of_a_deleted_account()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f);
        f.Robust.Remove("Test", "Admin");

        f.Time.Advance(TimeSpan.FromMinutes(5));
        await AssertSentToLoginAsync(client);

        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "session-ended");
        Assert.Equal("role changed", entry.GetProperty("target").GetString());
        Assert.Empty(f.Services.GetRequiredService<StaffSessions>().List(TestAuth.AdminId));
    }

    [Fact]
    public void Recheck_is_due_after_five_minutes_or_when_the_check_time_is_in_the_future()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.False(StaffPrincipalValidator.IsDue(now, now));
        Assert.False(StaffPrincipalValidator.IsDue(now - TimeSpan.FromSeconds(299), now));
        Assert.True(StaffPrincipalValidator.IsDue(now - TimeSpan.FromMinutes(5), now));
        Assert.True(StaffPrincipalValidator.IsDue(now + TimeSpan.FromSeconds(1), now)); // the clock was set back
    }

    [Fact]
    public async Task Recheck_with_same_role_renews_the_check()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f);
        var lookups = f.Robust.Lookups;

        f.Time.Advance(TimeSpan.FromMinutes(5));
        await AssertOkAsync(client);
        Assert.Equal(lookups + 1, f.Robust.Lookups);

        f.Time.Advance(TimeSpan.FromMinutes(1));
        await AssertOkAsync(client);
        Assert.Equal(lookups + 1, f.Robust.Lookups);
    }

    [Fact]
    public async Task Owner_is_never_demoted_by_the_recheck()
    {
        await using var f = Factory();
        var client = await SignedInAsync(f, "owner");
        f.Robust.Add("Test", "Owner", 0, "pass-word1", TestAuth.OwnerId);

        f.Time.Advance(TimeSpan.FromMinutes(5));

        await AssertOkAsync(client);
    }
}
