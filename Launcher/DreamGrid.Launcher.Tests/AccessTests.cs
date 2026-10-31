using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class AccessTests
{
    private const string Token = "tok-1234567890abcdefghijklmnopqrstuv";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherFactory Before() => new(s => s with { SetupToken = Token });

    private static LauncherFactory AfterOwner(bool finished = false) => new(s => s with
    {
        // Task 3 spends the token through UpdateSettings; this simulates the result.
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = finished },
    })
    {
        Before = TestAuth.Apply,
    };

    private static string? LocationPath(HttpResponseMessage r) =>
        r.Headers.Location is { IsAbsoluteUri: true } u ? u.AbsolutePath : r.Headers.Location?.OriginalString;

    private static HttpClient NoRedirect(LauncherFactory f) =>
        f.CreateClient(new() { AllowAutoRedirect = false });

    [Fact]
    public async Task Before_setup_root_redirects_to_waiting()
    {
        await using var f = Before();
        using var resp = await NoRedirect(f).GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/waiting", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Setup_with_wrong_token_redirects_to_waiting()
    {
        await using var f = Before();
        using var resp = await NoRedirect(f).GetAsync("/setup?token=nope", Ct);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/waiting", resp.Headers.Location?.OriginalString);
        Assert.False(resp.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Setup_with_right_token_sets_cookie_and_strips_token_from_url()
    {
        await using var f = Before();
        using var resp = await NoRedirect(f).GetAsync("/setup?token=" + Token, Ct);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/setup", resp.Headers.Location?.OriginalString);
        Assert.DoesNotContain("token=", resp.Headers.Location!.OriginalString);
        var cookie = Assert.Single(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-setup=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setup_cookie_lets_setup_pages_and_setup_api_through_but_not_other_pages()
    {
        await using var f = Before();
        using var client = NoRedirect(f);
        (await client.GetAsync("/setup?token=" + Token, Ct)).Dispose();

        // "Through" means the setup page and API answered, not the gate.
        using var setup = await client.GetAsync("/setup", Ct);
        using var api = await client.GetAsync("/api/setup/progress", Ct);
        using var other = await client.GetAsync("/", Ct);

        Assert.Equal("/setup/plan", setup.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Equal("/waiting", other.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Setup_api_without_cookie_redirects_to_waiting()
    {
        await using var f = Before();
        using var resp = await NoRedirect(f).GetAsync("/api/setup/progress", Ct);

        Assert.Equal("/waiting", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Welcome_waiting_and_status_stay_open_before_setup()
    {
        await using var f = Before();
        using var client = NoRedirect(f);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/welcome", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/waiting", Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, (await client.GetAsync("/api/status", Ct)).StatusCode);
    }

    [Fact]
    public async Task After_setup_is_finished_setup_link_shows_setup_done()
    {
        // Review Focus 2: a second device opening the old link after setup is told so, and nothing more.
        await using var f = AfterOwner(finished: true);
        using var client = NoRedirect(f);

        using var withToken = await client.GetAsync("/setup?token=" + Token, Ct);
        using var plain = await client.GetAsync("/setup", Ct);
        using var api = await client.GetAsync("/api/setup/progress", Ct);

        foreach (var r in new[] { withToken, plain, api })
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/setup-done", r.Headers.Location?.OriginalString);
            Assert.False(r.Headers.Contains("Set-Cookie"));
        }
        var page = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await client.GetStringAsync("/setup-done", Ct));
        Assert.Equal("Setup is already finished", page.QuerySelector("h1")?.TextContent);
        Assert.NotNull(page.QuerySelector("main a[href='/login']"));
        Assert.DoesNotContain("Setup is already finished", page.QuerySelector("main p")?.TextContent);
    }

    [Fact]
    public async Task Before_setup_is_finished_the_setup_link_asks_to_sign_in_first()
    {
        await using var f = AfterOwner(finished: false);
        using var client = NoRedirect(f);

        using var withToken = await client.GetAsync("/setup?token=" + Token, Ct);
        using var plain = await client.GetAsync("/setup/region", Ct);
        using var api = await client.GetAsync("/api/setup/progress", Ct);

        foreach (var r in new[] { withToken, plain })
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/login?ReturnUrl=%2Fsetup", r.Headers.Location?.OriginalString);
            Assert.False(r.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task Signed_in_admin_home_leads_to_setup_until_it_is_finished()
    {
        await using var f = AfterOwner(finished: false);
        using var client = NoRedirect(f);
        (await client.GetAsync("/__test/signin", Ct)).Dispose();

        using var resp = await client.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/setup", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task After_owner_exists_root_shows_the_front_page_and_staff_pages_require_sign_in()
    {
        await using var f = AfterOwner();
        using var root = await NoRedirect(f).GetAsync("/", Ct);
        using var dashboard = await NoRedirect(f).GetAsync("/dashboard", Ct);

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("href=\"/guest/sign-in\"", await root.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
        Assert.Equal("/login", LocationPath(dashboard));
    }

    [Fact]
    public async Task Challenge_redirects_pages_to_login_but_answers_api_paths_with_401()
    {
        await using var f = AfterOwner();
        using var client = NoRedirect(f);

        using var page = await client.GetAsync("/__test/challenge", Ct);
        using var api = await client.GetAsync("/api/__test/challenge", Ct);

        Assert.Equal("/login", LocationPath(page));
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task Signed_in_admin_reaches_setup_while_setup_is_not_finished()
    {
        await using var f = AfterOwner(finished: false);
        using var client = NoRedirect(f);
        (await client.GetAsync("/__test/signin", Ct)).Dispose();

        using var setup = await client.GetAsync("/setup", Ct);
        using var api = await client.GetAsync("/api/setup/progress", Ct);

        // Passed the gate: the checklist sends on to its first unfinished step (this seed never chose a plan).
        Assert.Equal("/setup/plan", setup.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
    }

    [Fact]
    public async Task Signed_in_admin_is_sent_to_setup_done_once_setup_is_finished()
    {
        await using var f = AfterOwner(finished: true);
        using var client = NoRedirect(f);
        (await client.GetAsync("/__test/signin", Ct)).Dispose();

        using var resp = await client.GetAsync("/setup", Ct);

        Assert.Equal("/setup-done", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Signed_in_admin_reaches_root()
    {
        await using var f = AfterOwner(finished: true);
        using var client = NoRedirect(f);
        (await client.GetAsync("/__test/signin", Ct)).Dispose();

        using var resp = await client.GetAsync("/", Ct);

        // The dashboard, not the legacy handler.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Your world", await resp.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_8001_routes_still_open(bool ownerExists)
    {
        await using var f = ownerExists ? AfterOwner() : Before();
        using var client = NoRedirect(f);

        using var command = await client.GetAsync("/?command=RegionStatus&RegionName=x", Ct);
        using var alt = await client.GetAsync("/?alt=x", Ct);
        using var type = await client.GetAsync("/?type=Booted&UUID=x", Ct);
        using var ready = await client.PostAsync("/dreamgrid/regionready", new StringContent(""), Ct);

        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alt.StatusCode);
        Assert.Equal(HttpStatusCode.OK, type.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, ready.StatusCode);
    }

    [Fact]
    public async Task The_setup_token_is_not_logged_when_it_is_used()
    {
        await using var f = Before();
        (await NoRedirect(f).GetAsync("/setup?token=" + Token, Ct)).Dispose();

        Assert.DoesNotContain(f.Logs, l => l.Message.Contains(Token) && !l.Message.StartsWith("Setup link:", StringComparison.Ordinal));
    }

    /// <summary>
    /// With the admin policy as the fallback, every endpoint is admin-only unless it carries IAllowAnonymous, so this
    /// list is the whole open surface. Minimal APIs carry it through .AllowAnonymous(); a Razor page carries it
    /// through the AllowAnonymousToPage/Folder conventions (they put IAllowAnonymous in the page endpoint's metadata,
    /// told apart here by its PageActionDescriptor). A new endpoint that is not listed fails this test.
    /// </summary>
    [Fact]
    public async Task Anonymous_endpoints_are_exactly_the_allowlist()
    {
        await using var f = Before();
        using var client = NoRedirect(f); // starts the host

        var open = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => (e.Metadata.GetMetadata<PageActionDescriptor>() is null ? "route " : "page ") + e.RoutePattern.RawText)
            .Distinct().Order(StringComparer.Ordinal).ToList();

        string[] allowlist =
        [
            // The port-8001 contract (the bare "/" is split from the dashboard by LegacyRootPolicy; SetupGate guards it).
            "route /",
            "route /dreamgrid/regionready",
            // Names and states only; avatar names only for an admin.
            "route /api/status",
            // Guarded by SetupGate, like the setup pages.
            "route /api/setup/progress",
            // The public front page: SetupGate shows it at "/" to anyone not signed in as staff, where there is a guest side.
            "page home",
            // The guest door: any account at level 0 or more signs in here, to the guest pages only.
            "page guest/sign-in",
            // The operator's legal pages (terms, privacy, cookies, contact) and the sign-up page: public, and 404 until written / where there is no guest side.
            "page {slug:regex(^(terms|privacy|cookies|contact)$)}",
            "page login",
            // The two-factor code step: only does anything with the pending cookie the password step leaves.
            "page login/code",
            "page logout",
            "page setup",
            "page setup-done",
            "page setup/avatar",
            "page setup/connect",
            "page setup/join",
            "page setup/network",
            "page setup/other-grid",
            "page setup/plan",
            "page setup/plan/{id}",
            "page setup/region",
            "page setup/world",
            "page waiting",
            "page signup",
            "page welcome",
        ];
        Assert.Equal(allowlist.Order(StringComparer.Ordinal), open);
    }
}
