using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The guest cookie opens the guest pages and nothing else; the staff cookie opens nothing of the guest side.</summary>
public sealed class GuestAccessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherFactory Factory(Func<LauncherSettings, LauncherSettings>? more = null)
    {
        var f = new LauncherFactory(s => (more ?? (x => x))(s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        }))
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<HttpClient> GuestAsync(LauncherFactory f)
    {
        var client = Client(f);
        using var r = await client.GetAsync("/__test/guest-signin", Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static async Task<HttpClient> StaffAsync(LauncherFactory f, string role = "admin")
    {
        var client = Client(f);
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static string? LocationPath(HttpResponseMessage r) =>
        r.Headers.Location is { IsAbsoluteUri: true } u ? u.AbsolutePath : r.Headers.Location?.OriginalString.Split('?')[0];

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/regions")]
    [InlineData("/logs")]
    [InlineData("/advanced")]
    public async Task Guest_cookie_refused_on_dashboard(string url)
    {
        await using var f = Factory();
        var guest = await GuestAsync(f);

        using var r = await guest.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login", LocationPath(r));
    }

    [Fact]
    public async Task Guest_cookie_refused_on_api()
    {
        await using var f = Factory();
        var guest = await GuestAsync(f);

        using var post = await guest.PostAsync("/api/grid/start", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);
        using var get = await guest.GetAsync("/api/logs?source=launcher", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
    }

    [Fact]
    public async Task Guest_cookie_refused_on_people_page()
    {
        await using var f = Factory();
        var guest = await GuestAsync(f);

        foreach (var url in new[] { "/people", "/profile/sessions", "/staff", "/security" })
        {
            using var r = await guest.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/login", LocationPath(r));
        }
    }

    [Fact]
    public async Task Staff_cookie_does_not_open_guest_home()
    {
        await using var f = Factory();
        foreach (var role in new[] { "owner", "admin", "supervisor" })
        {
            var staff = await StaffAsync(f, role);

            using var r = await staff.GetAsync("/guest", Ct);

            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/guest/sign-in", LocationPath(r));
        }
    }

    [Fact]
    public async Task Guest_home_shows_the_name_and_the_steps_for_firestorm()
    {
        await using var f = Factory();
        var guest = await GuestAsync(f);

        var page = new HtmlParser().ParseDocument(await guest.GetStringAsync("/guest", Ct));

        var main = page.QuerySelector("main")!.TextContent;
        Assert.Contains("You're signed in as Test Guest.", main);
        Assert.Contains("Connect with Firestorm", main);
        Assert.Equal("https://www.firestormviewer.org/downloads/", page.QuerySelector("main a[href^='https://www.firestormviewer.org']")!.GetAttribute("href"));
        var s = f.Store.LoadOrCreate();
        Assert.Equal(GridEndpoints.LoginUri(s, HostAddress.Effective(s.Hostname)), page.QuerySelector("main code")!.TextContent);
        Assert.Contains("Avalon", main);
        Assert.Empty(page.QuerySelectorAll("header nav a")); // no staff navigation
        Assert.DoesNotContain("href=\"/people\"", page.Body!.InnerHtml);
        Assert.DoesNotContain("href=\"/logout\"", page.Body!.InnerHtml);
    }

    [Fact]
    public async Task Guest_form_post_passes_antiforgery()
    {
        await using var f = Factory();
        var guest = await GuestAsync(f);
        var page = new HtmlParser().ParseDocument(await guest.GetStringAsync("/guest", Ct));
        var form = page.QuerySelector("form[action='/guest/sign-out']");
        Assert.NotNull(form);
        Assert.Equal("post", form.GetAttribute("method")?.ToLowerInvariant());
        var token = form.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);

        using (var get = await guest.GetAsync("/guest/sign-out", Ct))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using (var noToken = await guest.PostAsync("/guest/sign-out", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct))
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        using var post = await guest.PostAsync("/guest/sign-out",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/", post.Headers.Location?.OriginalString);
        using var after = await guest.GetAsync("/guest", Ct);
        Assert.Equal("/guest/sign-in", LocationPath(after));
    }

    [Fact]
    public async Task Guest_sign_out_leaves_the_staff_cookie_alone()
    {
        await using var f = Factory();
        var both = await StaffAsync(f);
        (await both.GetAsync("/__test/guest-signin", Ct)).Dispose();
        var page = new HtmlParser().ParseDocument(await both.GetStringAsync("/guest", Ct));
        var token = page.QuerySelector("form[action='/guest/sign-out'] input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        (await both.PostAsync("/guest/sign-out", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct)).Dispose();

        using var logs = await both.GetAsync("/logs", Ct);
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
        using var home = await both.GetAsync("/guest", Ct);
        Assert.Equal("/guest/sign-in", LocationPath(home));
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task Guest_pages_404_on_region_server_and_connected(OwnerPlan plan)
    {
        await using var f = Factory(s => s with
        {
            Plan = plan,
            Join = plan == OwnerPlan.RegionServer
                ? new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006) : null,
            Connected = plan == OwnerPlan.Connected
                ? new ConnectedGrid("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", null) : null,
            Hypergrid = false,
        });
        var guest = await GuestAsync(f);

        foreach (var url in new[] { "/guest", "/guest/sign-in", "/guest/sign-out" })
        {
            using var r = await guest.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        // Not signed in: the policy challenges first, and that answers 404 too, not a redirect.
        var anonymousClient = Client(f);
        foreach (var url in new[] { "/guest", "/guest/sign-in", "/guest/sign-out" })
        {
            using var r = await anonymousClient.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        using var signOut = await anonymousClient.PostAsync("/guest/sign-out", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);
        Assert.Equal(HttpStatusCode.NotFound, signOut.StatusCode);
    }
}
