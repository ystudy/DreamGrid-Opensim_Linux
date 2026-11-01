using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The New sign-ups card, the sign-ups list, the deny step and the menu badge (spec 2026-11-01-sign-up-approval-design §7, §8). All names are made up.</summary>
public sealed class SignUpBoardPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SignUpsTests.Rig Rig(SignUpApprovers who = SignUpApprovers.OwnerAndAdmins, OwnerPlan? plan = null)
    {
        var rig = SignUpsTests.Factory(email: false);
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s
            ? s with { Bots = s.Bots with { AdminApproval = true, Approvers = who }, Plan = plan ?? s.Plan }
            : null!);
        return rig;
    }

    /// <summary>Signs up <paramref name="n"/> people (Aa Example, Ab Example, …), one minute apart, all waiting for staff.</summary>
    private static async Task<List<Guid>> WaitingAsync(SignUpsTests.Rig rig, int n)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < n; i++)
        {
            var first = (i < 26 ? "A" : "B") + (char)('a' + i % 26);
            await rig.SignUps.SubmitAsync(SignUpsTests.Form(first: first, email: $"{first}@example.com"), null, Ct);
            ids.Add(rig.F.Robust.All.Single(a => a.FirstName == first).PrincipalId);
            rig.F.Time.Advance(TimeSpan.FromMinutes(1));
        }
        return ids;
    }

    private static async Task<HttpClient> SignedInAsync(SignUpsTests.Rig rig, string role)
    {
        var c = rig.F.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Token(IHtmlDocument page) =>
        page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value") ?? throw new Xunit.Sdk.XunitException("no antiforgery token");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string tokenFrom, string url, Dictionary<string, string> fields)
    {
        var page = await PageAsync(client, tokenFrom);
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = Token(page) }), Ct);
    }

    private static List<string> Names(IHtmlDocument page, string scope) =>
        page.QuerySelectorAll($"{scope} .signup-row .signup-name").Select(e => e.TextContent.Trim()).ToList();

    [Fact]
    public async Task Card_shows_waiting_first_and_ten_rows()
    {
        await using var rig = Rig();
        var ids = await WaitingAsync(rig, 12);
        rig.Pending.TryFinish(ids[0], SignUpState.Approved, "Test Owner");
        var owner = await SignedInAsync(rig, "owner");

        var page = await PageAsync(owner, "/");

        var names = Names(page, "#signups-card");
        Assert.Equal(10, names.Count);
        Assert.Equal("Ab Example", names[0]);
        Assert.DoesNotContain("Aa Example", names);
        Assert.Contains("11 waiting", page.QuerySelector("#signups-card h2")!.TextContent);
        Assert.NotNull(page.QuerySelector("#signups-card a[href='/people/sign-ups']"));
    }

    [Fact]
    public async Task Finished_rows_show_their_badges()
    {
        await using var rig = Rig();
        var ids = await WaitingAsync(rig, 2);
        rig.Pending.TryFinish(ids[0], SignUpState.Approved, "Test Owner");
        var owner = await SignedInAsync(rig, "owner");

        var text = (await PageAsync(owner, "/")).QuerySelector("#signups-card")!.TextContent;

        Assert.Contains("Approved by Test Owner", text);
        Assert.Contains("Waiting for approval", text);
        Assert.Contains("signed up", text);
    }

    [Theory]
    [InlineData(SignUpApprovers.OwnerAndAdmins, "admin", true)]
    [InlineData(SignUpApprovers.Owner, "admin", false)]
    [InlineData(SignUpApprovers.Owner, "owner", true)]
    public async Task Card_has_buttons_for_approvers_only(SignUpApprovers who, string role, bool buttons)
    {
        await using var rig = Rig(who);
        await WaitingAsync(rig, 1);
        var client = await SignedInAsync(rig, role);

        var page = await PageAsync(client, "/");

        Assert.Single(Names(page, "#signups-card"));
        Assert.Equal(buttons, page.QuerySelector("#signups-card button.approve") is not null);
        Assert.Equal(buttons, page.QuerySelector("#signups-card a.deny") is not null);
    }

    [Fact]
    public async Task Card_hides_unlisted()
    {
        await using var rig = SignUpsTests.Factory();
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), null, Ct);
        var owner = await SignedInAsync(rig, "owner");

        var page = await PageAsync(owner, "/");

        Assert.Empty(Names(page, "#signups-card"));
        Assert.DoesNotContain("Bea", page.QuerySelector("#signups-card")?.TextContent ?? "");
    }

    [Fact]
    public async Task Card_absent_on_region_server()
    {
        await using var rig = Rig(plan: OwnerPlan.RegionServer);
        var owner = await SignedInAsync(rig, "owner");

        using var r = await owner.GetAsync("/", Ct);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));

        Assert.Null(page.QuerySelector("#signups-card"));
        using var list = await owner.GetAsync("/people/sign-ups", Ct);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
    }

    [Fact]
    public async Task List_search_by_name()
    {
        await using var rig = Rig();
        await WaitingAsync(rig, 3);
        var admin = await SignedInAsync(rig, "admin");

        var page = await PageAsync(admin, "/people/sign-ups?q=ab");

        Assert.Equal(["Ab Example"], Names(page, "#signups-list"));
    }

    [Theory]
    [InlineData("name", "asc", new[] { "Aa Example", "Ab Example", "Ac Example" })]
    [InlineData("name", "desc", new[] { "Ac Example", "Ab Example", "Aa Example" })]
    [InlineData("made", "desc", new[] { "Ac Example", "Ab Example", "Aa Example" })]
    [InlineData("state", "asc", new[] { "Ab Example", "Ac Example", "Aa Example" })]
    public async Task List_sort_by_each_column(string sort, string dir, string[] expected)
    {
        await using var rig = Rig();
        var ids = await WaitingAsync(rig, 3);
        rig.Pending.TryFinish(ids[0], SignUpState.Approved, "Test Owner");
        var admin = await SignedInAsync(rig, "admin");

        var page = await PageAsync(admin, $"/people/sign-ups?sort={sort}&dir={dir}");

        Assert.Equal(expected, Names(page, "#signups-list"));
    }

    [Fact]
    public async Task List_pages_25()
    {
        await using var rig = Rig();
        await WaitingAsync(rig, 27);
        var admin = await SignedInAsync(rig, "admin");

        Assert.Equal(25, Names(await PageAsync(admin, "/people/sign-ups"), "#signups-list").Count);
        Assert.Equal(2, Names(await PageAsync(admin, "/people/sign-ups?p=2"), "#signups-list").Count);
    }

    [Fact]
    public async Task Approve_from_card_returns_to_dashboard()
    {
        await using var rig = Rig();
        var ids = await WaitingAsync(rig, 1);
        var owner = await SignedInAsync(rig, "owner");

        using var r = await PostAsync(owner, "/", "/people/sign-ups?handler=approve", new() { ["id"] = ids[0].ToString(), ["returnUrl"] = "/" });

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location?.OriginalString);
        Assert.Equal(SignUpState.Approved, rig.Pending.Get(ids[0])!.State);
        Assert.Equal(0, rig.F.Robust.All.Single(a => a.PrincipalId == ids[0]).UserLevel);
    }

    [Fact]
    public async Task Deny_asks_first_then_deletes()
    {
        await using var rig = Rig();
        var ids = await WaitingAsync(rig, 1);
        var owner = await SignedInAsync(rig, "owner");

        var ask = await PageAsync(owner, $"/people/sign-ups/deny?id={ids[0]}&returnUrl=/");
        Assert.Contains("Deny Aa Example?", ask.QuerySelector("main")!.TextContent);
        Assert.Equal("500", ask.QuerySelector("textarea[name=note]")!.GetAttribute("maxlength"));
        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == ids[0]);

        using var r = await owner.PostAsync($"/people/sign-ups/deny", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = ids[0].ToString(), ["note"] = "Club members only.", ["returnUrl"] = "/", ["__RequestVerificationToken"] = Token(ask),
        }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location?.OriginalString);
        Assert.DoesNotContain(rig.F.Robust.All, a => a.PrincipalId == ids[0]);
        Assert.Equal(SignUpState.Denied, rig.Pending.Get(ids[0])!.State);
    }

    [Fact]
    public async Task Supervisor_post_refused_when_admins_only()
    {
        await using var rig = Rig(SignUpApprovers.OwnerAndAdmins);
        var ids = await WaitingAsync(rig, 1);
        var supervisor = await SignedInAsync(rig, "supervisor");

        var list = await PageAsync(supervisor, "/people/sign-ups");
        Assert.Null(list.QuerySelector("button.approve"));
        using var r = await PostAsync(supervisor, "/people/sign-ups", "/people/sign-ups?handler=approve", new() { ["id"] = ids[0].ToString() });

        Assert.Equal(SignUpState.WaitingForStaff, rig.Pending.Get(ids[0])!.State);
        Assert.Equal(-1, rig.F.Robust.All.Single(a => a.PrincipalId == ids[0]).UserLevel);
    }

    [Fact]
    public async Task Supervisor_can_approve_from_list_when_allowed()
    {
        await using var rig = Rig(SignUpApprovers.OwnerAdminsAndSupervisors);
        var ids = await WaitingAsync(rig, 1);
        var supervisor = await SignedInAsync(rig, "supervisor");

        var list = await PageAsync(supervisor, "/people/sign-ups");
        Assert.NotNull(list.QuerySelector("button.approve"));
        using var r = await PostAsync(supervisor, "/people/sign-ups", "/people/sign-ups?handler=approve", new() { ["id"] = ids[0].ToString() });

        Assert.Equal("/people/sign-ups", r.Headers.Location?.OriginalString);
        Assert.Equal((SignUpState.Approved, "Test Supervisor"), (rig.Pending.Get(ids[0])!.State, rig.Pending.Get(ids[0])!.By));
    }

    [Fact]
    public async Task Supervisor_cannot_open_dashboard()
    {
        await using var rig = Rig(SignUpApprovers.OwnerAdminsAndSupervisors);
        var supervisor = await SignedInAsync(rig, "supervisor");

        using var r = await supervisor.GetAsync("/", Ct);

        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Post_without_antiforgery_rejected()
    {
        await using var rig = Rig();
        var ids = await WaitingAsync(rig, 1);
        var owner = await SignedInAsync(rig, "owner");

        using var r = await owner.PostAsync("/people/sign-ups?handler=approve",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = ids[0].ToString() }), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(SignUpState.WaitingForStaff, rig.Pending.Get(ids[0])!.State);
    }

    [Fact]
    public async Task Return_url_must_be_local()
    {
        await using var rig = Rig();
        var ids = await WaitingAsync(rig, 1);
        var owner = await SignedInAsync(rig, "owner");

        using var r = await PostAsync(owner, "/", "/people/sign-ups?handler=approve",
            new() { ["id"] = ids[0].ToString(), ["returnUrl"] = "https://evil.example/" });

        Assert.Equal("/people/sign-ups", r.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Outcome_message_shown_after_redirect()
    {
        await using var rig = Rig();
        var ids = await WaitingAsync(rig, 1);
        var owner = await SignedInAsync(rig, "owner");
        (await PostAsync(owner, "/people/sign-ups", "/people/sign-ups?handler=approve", new() { ["id"] = ids[0].ToString() })).Dispose();

        using var again = await PostAsync(owner, "/people/sign-ups", "/people/sign-ups?handler=approve", new() { ["id"] = ids[0].ToString() });

        Assert.Contains("Already done by Test Owner.", (await PageAsync(owner, "/people/sign-ups")).QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task Badge_counts_waiting()
    {
        await using var rig = Rig();
        await WaitingAsync(rig, 2);
        var admin = await SignedInAsync(rig, "admin");

        var badge = (await PageAsync(admin, "/regions")).QuerySelector("#signups-badge")!;

        Assert.Equal("2 waiting", badge.TextContent.Trim());
        Assert.Equal("/", badge.Closest("a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Badge_hidden_at_zero_and_for_non_approvers()
    {
        await using var rig = Rig(SignUpApprovers.Owner);
        var owner = await SignedInAsync(rig, "owner");
        Assert.Null((await PageAsync(owner, "/regions")).QuerySelector("#signups-badge"));

        await WaitingAsync(rig, 1);
        var admin = await SignedInAsync(rig, "admin");
        Assert.Null((await PageAsync(admin, "/regions")).QuerySelector("#signups-badge"));
    }

    [Fact]
    public async Task Badge_links_people_for_supervisor()
    {
        await using var rig = Rig(SignUpApprovers.OwnerAdminsAndSupervisors);
        await WaitingAsync(rig, 1);
        var supervisor = await SignedInAsync(rig, "supervisor");

        var badge = (await PageAsync(supervisor, "/people")).QuerySelector("#signups-badge")!;

        Assert.Equal("/people/sign-ups", badge.Closest("a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task People_page_shows_waiting_for_approval()
    {
        await using var rig = Rig();
        await WaitingAsync(rig, 1);
        var admin = await SignedInAsync(rig, "admin");

        var page = await PageAsync(admin, "/people?Name=Aa+Example");

        Assert.Contains("Waiting for approval", page.QuerySelector("#person")!.TextContent);
        Assert.NotNull(page.QuerySelector("a[href='/people/sign-ups']"));
    }

    [Fact]
    public async Task No_inline_script()
    {
        await using var rig = Rig();
        await WaitingAsync(rig, 1);
        var owner = await SignedInAsync(rig, "owner");

        foreach (var url in new[] { "/", "/people/sign-ups", $"/people/sign-ups/deny?id={rig.Pending.Shown()[0].Principal}" })
        {
            using var r = await owner.GetAsync(url, Ct);
            var html = await r.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase), html);
            Assert.DoesNotMatch(new Regex(@"\son[a-z]+=|\sstyle=", RegexOptions.IgnoreCase), html);
        }
    }
}
