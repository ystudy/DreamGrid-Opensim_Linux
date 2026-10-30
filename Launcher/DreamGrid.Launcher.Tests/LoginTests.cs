using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Wrong = "Name or password is wrong, or this avatar is not an administrator.";
    private const string Locked = "Too many attempts. Try again in a few minutes.";
    private static readonly Guid OwnerId = Guid.NewGuid();

    private static LauncherFactory Factory(Action<IApplicationBuilder>? before = null)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true,
                Owner: new OwnerInfo("Test", "Owner", OwnerId)),
        })
        {
            Before = before,
        };
        f.Robust.Add("Test", "Owner", 200, "pass-word1", OwnerId).Add("Plain", "Person", 0, "pass-word1");
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
    public async Task Ip_lock_refuses_correct_password()
    {
        var remote = IPAddress.Parse("10.1.2.3");
        await using var f = Factory(app => app.Use((ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = remote;
            return next(ctx);
        }));
        var client = Client(f);
        for (var i = 0; i < 20; i++)
            Assert.Equal(Wrong, await ErrorAsync(await LoginAsync(client, $"Guess{i} Person", "bad-password")));
        var lookups = f.Robust.Lookups;

        var r = await LoginAsync(client, "Test Owner", "pass-word1");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(Locked, await ErrorAsync(r));
        Assert.Equal(lookups, f.Robust.Lookups);
    }

    [Fact]
    public async Task Ip_lock_does_not_reach_other_addresses()
    {
        var remote = IPAddress.Parse("10.1.2.3");
        await using var f = Factory(app => app.Use((ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = ctx.Request.Headers.ContainsKey("X-Test-Far") ? IPAddress.Parse("10.9.9.9") : remote;
            return next(ctx);
        }));
        var client = Client(f);
        for (var i = 0; i < 20; i++)
            await LoginAsync(client, $"Guess{i} Person", "bad-password");

        var far = Client(f);
        far.DefaultRequestHeaders.Add("X-Test-Far", "1");
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(far, "Test Owner", "pass-word1")).StatusCode);
    }

    [Fact]
    public async Task Different_spacings_of_one_name_share_one_lock()
    {
        await using var f = Factory();
        var client = Client(f);
        string[] spellings = ["Test Owner", "Test  Owner", "Test   Owner", " Test Owner ", "test owner"];
        foreach (var s in spellings)
            Assert.Equal(Wrong, await ErrorAsync(await LoginAsync(client, s, "bad-password")));
        foreach (var s in spellings)
            Assert.Equal(Locked, await ErrorAsync(await LoginAsync(client, s, "pass-word1")));
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

    /// <summary>The audit log's text (the host keeps the file open for writing).</summary>
    private static string AuditText(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    private static List<System.Text.Json.JsonElement> Audit(LauncherFactory f) =>
        AuditText(f).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone()).ToList();

    [Fact]
    public async Task Sign_in_is_audited_without_password()
    {
        await using var f = Factory();
        await LoginAsync(Client(f), "Test Owner", "pass-word1");
        await LoginAsync(Client(f), "Te\u0007st Owner", "secret-pass-9");
        await LoginAsync(Client(f), "my-secret-pw", "secret-pass-9"); // a password typed into the name field

        var entries = Audit(f);
        var ok = Assert.Single(entries, e => e.GetProperty("action").GetString() == "sign-in");
        Assert.Equal("Test Owner", ok.GetProperty("actor").GetString());
        Assert.Equal("owner", ok.GetProperty("role").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.String, ok.GetProperty("actorId").ValueKind);

        var refused = entries.Where(e => e.GetProperty("action").GetString() == "sign-in-refused").ToList();
        Assert.Equal(2, refused.Count);
        Assert.Equal("Te?st", refused[0].GetProperty("actor").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, refused[0].GetProperty("actorId").ValueKind);
        Assert.Equal("(an incomplete name)", refused[1].GetProperty("actor").GetString());

        var text = AuditText(f);
        Assert.DoesNotContain("pass-word1", text);
        Assert.DoesNotContain("secret-pass-9", text);
        Assert.DoesNotContain("my-secret-pw", text);
        Assert.DoesNotContain("Owner\"", string.Join('\n', refused.Select(e => e.GetRawText()))); // only the first name
    }

    [Fact]
    public async Task Staff_post_is_audited_without_query()
    {
        await using var f = Factory();
        var client = Client(f);
        await LoginAsync(client, "Test Owner", "pass-word1");

        await client.PostAsync("/dashboard?handler=Start&password=x", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);
        await client.GetAsync("/logs?source=launcher&password=y", Ct); // a GET is not audited

        var requests = Audit(f).Where(e => e.GetProperty("action").GetString() == "request").ToList();
        var entry = Assert.Single(requests);
        Assert.Equal("POST /dashboard", entry.GetProperty("target").GetString());
        Assert.Equal("Test Owner", entry.GetProperty("actor").GetString());
        Assert.Equal("owner", entry.GetProperty("role").GetString());
        Assert.Matches("^[0-9]{3}$", entry.GetProperty("result").GetString()!);
        var text = AuditText(f);
        Assert.DoesNotContain("password=x", text);
        Assert.DoesNotContain("password=y", text);
    }

    [Fact]
    public async Task Logs_page_lists_and_shows_the_audit_log()
    {
        await using var f = Factory();
        var client = Client(f);
        await LoginAsync(client, "Test Owner", "pass-word1");

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/logs?source=audit", Ct));

        Assert.Contains(page.QuerySelectorAll("#log-source option"), o => o.GetAttribute("value") == "audit");
        Assert.Contains("\"action\":\"sign-in\"", page.QuerySelector("#log-lines")!.TextContent);
    }

    [Fact]
    public async Task A_cookie_without_a_principal_claim_is_audited_with_no_actor_id()
    {
        await using var f = Factory(TestAuth.Apply);
        var client = Client(f);
        await client.GetAsync("/__test/signin", Ct);

        await client.PostAsync("/dashboard", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);

        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "request");
        Assert.Equal("owner", entry.GetProperty("actor").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, entry.GetProperty("actorId").ValueKind);
        Assert.Equal("admin", entry.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Anonymous_post_is_not_audited_as_staff()
    {
        await using var f = Factory();
        await Client(f).PostAsync("/dashboard", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);

        Assert.DoesNotContain(Audit(f), e => e.GetProperty("action").GetString() == "request");
    }

    [Fact]
    public async Task A_refused_sign_in_logs_only_the_cleaned_first_name()
    {
        await using var f = Factory();
        await LoginAsync(Client(f), "Te\u0007st Owner", "pass-word1");
        await LoginAsync(Client(f), "my-secret-pw", "pass-word1"); // a password typed into the name field

        var refused = f.Logs.Where(l => l.Message.Contains("refused")).Select(l => l.Message).ToList();
        Assert.Equal(2, refused.Count);
        Assert.Contains(refused, m => m.Contains("Te?st"));
        Assert.DoesNotContain(refused, m => m.Contains("Owner") || m.Contains('\u0007'));
        Assert.DoesNotContain(f.Logs, l => l.Message.Contains("my-secret-pw"));
    }
}
