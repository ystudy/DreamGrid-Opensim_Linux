using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Wrong = "Name or password is wrong, or this avatar is not an administrator.";
    private const string Locked = "Too many attempts. Try again in a few minutes.";

    private static LauncherFactory Factory()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true,
                Owner: new OwnerInfo("Test", "Owner", Guid.NewGuid())),
        });
        f.Robust.Add("Test", "Owner", 200, "pass-word1").Add("Plain", "Person", 0, "pass-word1");
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<string> TokenAsync(HttpClient client, string url = "/login")
    {
        var page = await client.GetStringAsync(url, Ct);
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string name, string password, string url = "/login")
    {
        var form = new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Password"] = password,
            ["__RequestVerificationToken"] = await TokenAsync(client),
        };
        return await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage r) =>
        new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector(".error")?.TextContent.Trim();

    [Fact]
    public async Task Valid_admin_signs_in_and_reaches_dashboard()
    {
        await using var f = Factory();
        var client = Client(f);
        var r = await LoginAsync(client, "Test Owner", "pass-word1");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location?.OriginalString);
        // The dashboard itself arrives in a later task; until then "/" is simply not sent back to the sign-in page.
        var home = await client.GetAsync("/", Ct);
        Assert.NotEqual(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/logs", Ct)).StatusCode);
    }

    [Fact]
    public async Task Local_return_url_is_followed_and_other_urls_are_not()
    {
        await using var f = Factory();
        var client = Client(f);
        var r = await LoginAsync(client, "Test Owner", "pass-word1", "/login?ReturnUrl=%2Flogs");
        Assert.Equal("/logs", r.Headers.Location?.OriginalString);

        var other = Client(f);
        var r2 = await LoginAsync(other, "Test Owner", "pass-word1", "/login?ReturnUrl=https%3A%2F%2Fevil.example%2F");
        Assert.Equal("/", r2.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Level_0_avatar_gets_generic_message()
    {
        await using var f = Factory();
        var r = await LoginAsync(Client(f), "Plain Person", "pass-word1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(Wrong, await ErrorAsync(r));
    }

    [Fact]
    public async Task Wrong_password_gets_identical_message()
    {
        await using var f = Factory();
        var level0 = await ErrorAsync(await LoginAsync(Client(f), "Plain Person", "pass-word1"));
        var wrong = await ErrorAsync(await LoginAsync(Client(f), "Test Owner", "not-the-password"));
        var unknown = await ErrorAsync(await LoginAsync(Client(f), "Nobody Here", "pass-word1"));
        var noLast = await ErrorAsync(await LoginAsync(Client(f), "Madonna", "pass-word1"));
        Assert.Equal(Wrong, wrong);
        Assert.Equal(level0, wrong);
        Assert.Equal(wrong, unknown);
        Assert.Equal(wrong, noLast);
    }

    [Fact]
    public async Task Locked_name_never_reaches_the_grid_and_is_told_so()
    {
        await using var f = Factory();
        var client = Client(f);
        for (var i = 0; i < 5; i++)
            Assert.Equal(Wrong, await ErrorAsync(await LoginAsync(client, "Test Owner", "bad-password")));
        var lookups = f.Robust.Lookups;

        var r = await LoginAsync(client, "test owner", "pass-word1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(Locked, await ErrorAsync(r));
        Assert.Equal(lookups, f.Robust.Lookups);
    }

    [Fact]
    public async Task Grid_not_running_gets_its_own_message_and_is_not_counted()
    {
        await using var f = Factory();
        f.Robust.Down = true;
        var client = Client(f);
        for (var i = 0; i < 6; i++)
        {
            var r = await LoginAsync(client, "Test Owner", "pass-word1");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("Your world's sign-in service isn't running yet. Start your world, then try again.", await ErrorAsync(r));
        }
        f.Robust.Down = false;
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, "Test Owner", "pass-word1")).StatusCode);
    }

    [Fact]
    public async Task Password_is_never_logged()
    {
        await using var f = Factory();
        await LoginAsync(Client(f), "Test Owner", "secret-pass-9");
        await LoginAsync(Client(f), "Test Owner", "pass-word1");
        Assert.DoesNotContain(f.Logs, l => l.Message.Contains("secret-pass-9") || l.Message.Contains("pass-word1"));
    }

    [Fact]
    public async Task Logout_requires_post_and_clears_cookie()
    {
        await using var f = Factory();
        var client = Client(f);
        await LoginAsync(client, "Test Owner", "pass-word1");

        var get = await client.GetAsync("/logout", Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/logs", Ct)).StatusCode);

        // The layout's Sign out button is a POST form carrying the antiforgery token.
        var logs = new HtmlParser().ParseDocument(await client.GetStringAsync("/logs", Ct));
        var form = logs.QuerySelector("form[action='/logout']");
        Assert.NotNull(form);
        Assert.Equal("post", form.GetAttribute("method")?.ToLowerInvariant());
        var token = form.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);

        var post = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/login", post.Headers.Location?.OriginalString);

        var after = await client.GetAsync("/logs", Ct);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Equal("/login", after.Headers.Location?.AbsolutePath);
    }
}
