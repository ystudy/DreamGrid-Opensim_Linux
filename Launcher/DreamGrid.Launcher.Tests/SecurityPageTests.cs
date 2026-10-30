using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's Security page, the admin network list in front of the whole site, and resetting someone's two-factor.</summary>
public sealed class SecurityPageTests
{
    private const string Header = "X-Test-Ip";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherFactory Factory(IReadOnlyList<string>? networks = null, bool finished = true) =>
        new(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = finished, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Security = s.Security with { AdminNetworks = networks },
        })
        {
            // The caller's address is whatever X-Test-Ip says (Kestrel would report it; the test server has none).
            Before = app =>
            {
                app.Use((ctx, next) =>
                {
                    if (ctx.Request.Headers.TryGetValue(Header, out var ip)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                    return next(ctx);
                });
                TestAuth.Apply(app);
            },
        };

    private static HttpClient Client(LauncherFactory f, string? ip = null)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false });
        if (ip is not null) c.DefaultRequestHeaders.Add(Header, ip);
        return c;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role, string? ip = null)
    {
        TestAuth.AddStaff(f.Robust);
        var c = Client(f, ip);
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, Dictionary<string, string> fields)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/security", Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync("/security?handler=" + handler, new FormUrlEncodedContent(form), Ct);
    }

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static string TextOf(string html) => new HtmlParser().ParseDocument(html).Body!.TextContent;

    [Theory]
    [InlineData("supervisor")]
    [InlineData("admin")]
    public async Task Supervisor_and_admin_get_403(string role)
    {
        await using var f = Factory();
        var client = await SignedInAsync(f, role);

        using var get = await client.GetAsync("/security", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);

        using var post = await client.PostAsync("/security?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(new SecuritySettings(), f.Store.LoadOrCreate().Security);
    }

    [Fact]
    public async Task Anonymous_is_sent_to_sign_in_and_owner_reaches_the_page()
    {
        await using var f = Factory();
        using var anon = await Client(f).GetAsync("/security", Ct);
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);

        var owner = await SignedInAsync(f, "owner");
        var html = await owner.GetStringAsync("/security", Ct);
        Assert.Contains("Require two-factor sign-in for admins and supervisors", html);
        Assert.Contains("href=\"/security\"", await owner.GetStringAsync("/profile/sessions", Ct)); // the owner's menu has the link

        var admin = await SignedInAsync(f, "admin");
        Assert.DoesNotContain("href=\"/security\"", await admin.GetStringAsync("/profile/sessions", Ct));
    }

    [Fact]
    public async Task Save_refuses_list_that_excludes_the_caller()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner", "10.1.1.1");

        using var r = await PostAsync(owner, "Save", new() { ["Networks"] = "192.168.0.0/16" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync(Ct);
        Assert.Contains("This list would lock out the address you're using now (10.1.1.1).", TextOf(html));
        Assert.Contains("192.168.0.0/16", html); // what was typed stays
        Assert.Null(f.Store.LoadOrCreate().Security.AdminNetworks);
        Assert.DoesNotContain(Audit(f), e => e.GetProperty("action").GetString() == "security-settings");
    }

    [Fact]
    public async Task Save_stores_list_that_includes_the_caller_and_applies_at_once()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner", "10.1.1.1");

        using var r = await PostAsync(owner, "Save", new() { ["Networks"] = "10.0.0.0/8\r\n\r\n 203.0.113.7 \r\n" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(["10.0.0.0/8", "203.0.113.7"], f.Store.LoadOrCreate().Security.AdminNetworks);
        Assert.Equal([false], f.Grid.UpdateWriteConfig);
        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "security-settings");
        Assert.Equal("admin-networks=2 entries", entry.GetProperty("target").GetString());
        Assert.Equal("owner", entry.GetProperty("role").GetString());
        Assert.Equal("10.1.1.1", entry.GetProperty("ip").GetString());

        // Another address is now outside; the list can be cleared from inside.
        using var outside = await Client(f, "203.0.113.99").GetAsync("/login", Ct);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        using var cleared = await PostAsync(owner, "Save", new() { ["Networks"] = "" });
        Assert.Null(f.Store.LoadOrCreate().Security.AdminNetworks);
        using var after = await Client(f, "203.0.113.99").GetAsync("/login", Ct);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Save_reports_a_bad_entry_and_changes_nothing()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner", "10.1.1.1");

        using var r = await PostAsync(owner, "Save", new() { ["Networks"] = "10.0.0.0/8\n10.0.0.0/33", ["RequireTwoFactor"] = "true" });

        Assert.Contains("10.0.0.0/33", await r.Content.ReadAsStringAsync(Ct));
        Assert.Equal(new SecuritySettings(), f.Store.LoadOrCreate().Security);
    }

    [Fact]
    public async Task Outside_caller_gets_404_on_login_and_api_and_command_api()
    {
        await using var f = Factory(["10.0.0.0/8"]);
        var outside = Client(f, "203.0.113.5");

        foreach (var url in new[] { "/login", "/", "/dashboard", "/api/status", "/api/grid/status", "/security", "/?alt=Region&type=Command&command=help", "/dreamgrid/regionready" })
        {
            using var get = await outside.GetAsync(url, Ct);
            Assert.True(HttpStatusCode.NotFound == get.StatusCode, $"GET {url} gave {(int)get.StatusCode}");
            Assert.Equal("", await get.Content.ReadAsStringAsync(Ct)); // nothing that says what this site is
        }
        using var post = await outside.PostAsync("/?alt=Region&type=Command&command=help", new StringContent(""), Ct);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        using var login = await outside.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);

        // Static files are served before the list is asked; the setup pages are never hidden by it.
        using var css = await outside.GetAsync("/css/site.css", Ct);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        using var setup = await outside.GetAsync("/setup", Ct);
        Assert.NotEqual(HttpStatusCode.NotFound, setup.StatusCode);
    }

    [Fact]
    public async Task Outside_cookie_does_not_help_and_does_not_reveal_whether_it_is_valid()
    {
        await using var f = Factory(["10.0.0.0/8"]);
        var owner = await SignedInAsync(f, "owner", "10.2.2.2");
        using var inside = await owner.GetAsync("/security", Ct);
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/security");
        request.Headers.Add(Header, "203.0.113.5");
        owner.DefaultRequestHeaders.Remove(Header);
        using var outside = await owner.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }

    [Theory]
    [InlineData("10.2.3.4")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:10.2.3.4")]
    public async Task Inside_caller_reaches_login(string ip)
    {
        await using var f = Factory(["10.0.0.0/8"]);

        using var r = await Client(f, ip).GetAsync("/login", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task No_list_means_every_address_reaches_login()
    {
        await using var f = Factory();
        using var r = await Client(f, "203.0.113.5").GetAsync("/login", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Reset_turns_off_and_ends_sessions()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");
        var admin = await SignedInAsync(f, "admin");
        var twoFactor = f.Services.GetRequiredService<TwoFactorStore>();
        var (secret, _) = twoFactor.BeginEnrol(TestAuth.AdminId, "Test Admin", "DreamGrid");
        Assert.NotNull(twoFactor.ConfirmEnrol(TestAuth.AdminId, Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow())), secret));
        using (var ok = await admin.GetAsync("/profile/sessions", Ct))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var html = await owner.GetStringAsync("/security", Ct);
        Assert.Contains("Test Admin", html);

        using var r = await PostAsync(owner, "Reset", new() { ["id"] = TestAuth.AdminId.ToString() });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(TwoFactorState.Off, twoFactor.StateOf(TestAuth.AdminId));
        Assert.Empty(f.Services.GetRequiredService<StaffSessions>().List(TestAuth.AdminId));
        using var gone = await admin.GetAsync("/profile/sessions", Ct);
        Assert.Equal(HttpStatusCode.Redirect, gone.StatusCode); // signed out
        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "two-factor-reset");
        Assert.Equal("Test Owner", entry.GetProperty("actor").GetString());
        Assert.Equal("owner", entry.GetProperty("role").GetString());
        Assert.Equal("Test Admin", entry.GetProperty("target").GetString());
        Assert.Equal("ok", entry.GetProperty("result").GetString());
        Assert.DoesNotContain("Test Admin", await owner.GetStringAsync("/security", Ct)); // no longer listed
    }

    [Fact]
    public async Task Reset_of_someone_without_two_factor_changes_nothing()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");
        var admin = await SignedInAsync(f, "admin");

        using var r = await PostAsync(owner, "Reset", new() { ["id"] = TestAuth.AdminId.ToString() });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var still = await admin.GetAsync("/profile/sessions", Ct);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
        Assert.DoesNotContain(Audit(f), e => e.GetProperty("action").GetString() == "two-factor-reset");
    }

    [Fact]
    public async Task Switch_change_is_audited()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");

        using var on = await PostAsync(owner, "Save", new() { ["RequireTwoFactor"] = "true", ["Networks"] = "" });
        Assert.True(f.Store.LoadOrCreate().Security.RequireTwoFactorForStaff);
        Assert.Contains("checked", await owner.GetStringAsync("/security", Ct));
        using var off = await PostAsync(owner, "Save", new() { ["Networks"] = "" });
        Assert.False(f.Store.LoadOrCreate().Security.RequireTwoFactorForStaff);
        using var same = await PostAsync(owner, "Save", new() { ["Networks"] = "" });

        var targets = Audit(f).Where(e => e.GetProperty("action").GetString() == "security-settings")
            .Select(e => e.GetProperty("target").GetString()).ToList();
        Assert.Equal(["require-two-factor=on", "require-two-factor=off"], targets); // a save that changes nothing is not a change
        Assert.Equal([false, false, false], f.Grid.UpdateWriteConfig);
    }
}
