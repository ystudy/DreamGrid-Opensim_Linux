using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The bare "/": the public front page for people who are not signed in as staff, and unchanged for everyone else.</summary>
public sealed class FrontPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherFactory Factory(OwnerPlan? plan = null, bool finished = true)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = plan,
            Setup = s.Setup with { OwnerCreated = true, Finished = finished, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<HttpClient> StaffAsync(LauncherFactory f, string role)
    {
        var client = Client(f);
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static string? LocationPath(HttpResponseMessage r) =>
        r.Headers.Location is { IsAbsoluteUri: true } u ? u.AbsolutePath : r.Headers.Location?.OriginalString;

    [Theory]
    [InlineData(null, true)]
    [InlineData(OwnerPlan.Local, true)]
    [InlineData(OwnerPlan.SimplePublic, true)]
    [InlineData(OwnerPlan.GridAsset, true)]
    [InlineData(OwnerPlan.Local, false)] // an anonymous visitor sees the front page even while setup is unfinished
    public async Task Anonymous_root_shows_front_page(OwnerPlan? plan, bool finished)
    {
        await using var f = Factory(plan, finished);

        using var r = await Client(f).GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // answered at "/", not redirected
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Avalon", page.QuerySelector("main")!.TextContent);
        Assert.Equal("/guest/sign-in", page.QuerySelector("main a[href='/guest/sign-in']")!.GetAttribute("href"));
        Assert.Equal("Create an account", page.QuerySelector("main a[href='/signup']")!.TextContent.Trim());
        var staff = page.QuerySelector("a[href='/login']")!;
        Assert.Equal("Staff sign in", staff.TextContent.Trim());
        Assert.Empty(page.QuerySelectorAll("header nav a")); // no staff navigation
        Assert.DoesNotContain("Your world", page.Body!.TextContent);
    }

    [Fact]
    public async Task Front_page_is_also_its_own_address()
    {
        await using var f = Factory();

        using var r = await Client(f).GetAsync("/home", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Create an account", await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Signed_in_guest_root_shows_front_page()
    {
        await using var f = Factory();
        var client = Client(f);
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();

        using var r = await client.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Create an account", await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Signed_in_admin_root_shows_dashboard()
    {
        await using var f = Factory();
        var admin = await StaffAsync(f, "admin");

        using var r = await admin.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Your world", html);
        Assert.DoesNotContain("Create an account", html);
    }

    [Fact]
    public async Task Signed_in_admin_root_goes_to_setup_until_it_is_finished()
    {
        await using var f = Factory(finished: false);
        var admin = await StaffAsync(f, "admin");

        using var r = await admin.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/setup", LocationPath(r));
    }

    [Fact]
    public async Task Supervisor_root_goes_to_people()
    {
        await using var f = Factory();
        var supervisor = await StaffAsync(f, "supervisor");

        using var r = await supervisor.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/people", LocationPath(r));
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task Root_on_region_server_challenges_as_today(OwnerPlan plan)
    {
        await using var f = Factory(plan);

        using var r = await Client(f).GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login", LocationPath(r));
        using var home = await Client(f).GetAsync("/home", Ct);
        Assert.Equal(HttpStatusCode.NotFound, home.StatusCode); // the front page does not exist where there is no guest side
    }

    [Fact]
    public async Task Root_before_the_owner_exists_still_goes_to_waiting()
    {
        await using var f = new LauncherFactory(s => s with { SetupToken = "tok-1234567890abcdefghijklmnopqrstuv" })
        {
            Before = TestAuth.Apply,
        };

        using var r = await Client(f).GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/waiting", LocationPath(r));
    }

    [Fact]
    public async Task Legacy_root_post_unchanged()
    {
        await using var f = Factory();
        var machineId = f.Store.LoadOrCreate().MachineId;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["COMMAND"] = "StopMysql", ["Password"] = machineId });

        using var post = await Client(f).PostAsync("/", form, Ct);
        using var alt = await Client(f).PostAsync("/?alt=Region", new StringContent(""), Ct);
        using var get = await Client(f).GetAsync("/?type=Booted", Ct);

        Assert.Equal("ACK", await post.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Stop database", f.Grid.Snapshot());
        Assert.Equal("ACK", await get.Content.ReadAsStringAsync(Ct));
        Assert.Equal("text/plain", alt.Content.Headers.ContentType?.MediaType); // the legacy handler answered, not the front page
    }

    [Fact]
    public async Task Dashboard_route_still_admin_only()
    {
        await using var f = Factory();

        using var anon = await Client(f).GetAsync("/dashboard", Ct);
        var supervisor = await StaffAsync(f, "supervisor");
        using var sup = await supervisor.GetAsync("/dashboard", Ct);
        var admin = await StaffAsync(f, "admin");
        using var ok = await admin.GetAsync("/dashboard", Ct);

        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
        Assert.Equal("/login", LocationPath(anon));
        Assert.Equal(HttpStatusCode.Forbidden, sup.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("Your world", await ok.Content.ReadAsStringAsync(Ct));
    }
}
