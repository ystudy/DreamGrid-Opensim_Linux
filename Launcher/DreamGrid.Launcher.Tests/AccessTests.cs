using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
        // Test-only helpers that go through the real "dg" cookie scheme.
        Before = app => app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/__test/signin")
            {
                var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner"), new Claim(ClaimTypes.Role, "admin")], "dg");
                await ctx.SignInAsync("dg", new ClaimsPrincipal(id));
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            }
            else if (ctx.Request.Path.Value!.EndsWith("/__test/challenge", StringComparison.Ordinal))
            {
                await ctx.ChallengeAsync("dg");
            }
            else
            {
                await next(ctx);
            }
        }),
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
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setup_cookie_lets_setup_pages_and_setup_api_through_but_not_other_pages()
    {
        await using var f = Before();
        using var client = NoRedirect(f);
        (await client.GetAsync("/setup?token=" + Token, Ct)).Dispose();

        // No Setup page exists yet (Task 3): "through" means the gate did not redirect.
        using var setup = await client.GetAsync("/setup", Ct);
        using var api = await client.GetAsync("/api/setup/progress", Ct);
        using var other = await client.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.NotFound, setup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
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
    public async Task After_owner_exists_setup_link_shows_setup_done()
    {
        await using var f = AfterOwner();
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
        var page = await client.GetStringAsync("/setup-done", Ct);
        Assert.Contains("Setup is already finished", page);
    }

    [Fact]
    public async Task After_owner_exists_root_requires_sign_in()
    {
        await using var f = AfterOwner();
        using var resp = await NoRedirect(f).GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/login", LocationPath(resp));
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

        // Passed the gate; the Setup page and API come in Task 3.
        Assert.Equal(HttpStatusCode.NotFound, setup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
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

        // No Index page yet (Task 5); what matters is that the legacy probe no longer answers a bare "/".
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.DoesNotContain("Test Completed", await resp.Content.ReadAsStringAsync(Ct));
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
}
