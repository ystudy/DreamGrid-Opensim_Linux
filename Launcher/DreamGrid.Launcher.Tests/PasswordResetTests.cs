using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Password recovery through the real website: the Forgot password page and the reset page its email links to.</summary>
public sealed class PasswordResetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public const string Reply = "If that name and email match an account, we've sent a link. It works once, for 30 minutes. Check your spam folder.";
    public const string GridOff = "Password reset isn't turned on for this grid. Contact the grid owner.";

    private static readonly Guid AnnId = Guid.Parse("0a110000-0000-0000-0000-0000000000a1");
    private static readonly Guid AdamId = Guid.Parse("0a120000-0000-0000-0000-0000000000a2");
    private static readonly IPAddress Caller = IPAddress.Parse("192.168.1.50");

    private sealed record Rig(LauncherFactory F, EmailPageTests.FakeTransport Transport) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    private static Rig Factory(RecoverySettings? rules = null, OwnerPlan plan = OwnerPlan.Local, bool email = true,
        IReadOnlyList<string>? adminNetworks = null, Func<LauncherSettings, LauncherSettings>? also = null)
    {
        var transport = new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s =>
        {
            s = s with
            {
                GridName = "Avalon",
                SetupToken = null,
                Plan = plan,
                Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
                Recovery = rules ?? new RecoverySettings(RecoveryRule.EmailLink, RecoveryRule.EmailLink, RecoveryRule.EmailLink),
                Bots = new BotSettings(Honeypot: true, RateLimit: false, ProofOfWork: false),
                Security = s.Security with { AdminNetworks = adminNetworks },
            };
            return also is null ? s : also(s);
        })
        {
            Before = app =>
            {
                app.Use((ctx, next) =>
                {
                    ctx.Connection.RemoteIpAddress = Caller;
                    return next(ctx);
                });
                TestAuth.Apply(app);
            },
            ExtraServices = services => services.AddSingleton<IMailTransport>(transport),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Smith", 0, "old-password-1", AnnId, email: "ann@example.com");
        f.Robust.Add("Adam", "Admin", 200, "old-password-1", AdamId, email: "adam@example.com");
        f.Robust.Add("Sue", "Spended", -1, "old-password-1", email: "sue@example.com");
        if (email)
        {
            var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
                "grid@gmail.com", null, 500, null, false, 0, null, null);
            f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
        }
        return new Rig(f, transport);
    }

    private static HttpClient Client(Rig r) => r.F.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Token(IHtmlDocument page)
    {
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    internal static async Task<IHtmlDocument> AskAsync(HttpClient client, string name, string email, string honeypot = "")
    {
        var page = await PageAsync(client, "/guest/forgot-password");
        using var r = await client.PostAsync("/guest/forgot-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = name, ["Email"] = email, ["website"] = honeypot, ["__RequestVerificationToken"] = Token(page),
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string? ReplyOf(IHtmlDocument page) => page.QuerySelector("#reset-reply")?.TextContent.Trim();

    private static async Task<List<(EmailAccount Account, string Password, string To, RenderedEmail Email)>> SentAsync(Rig rig, int atLeast)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (rig.Transport.Sent)
                if (rig.Transport.Sent.Count >= atLeast) return [.. rig.Transport.Sent];
            await Task.Delay(25, Ct);
        }
        lock (rig.Transport.Sent) return [.. rig.Transport.Sent];
    }

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static int Actions(LauncherFactory f, string action) =>
        Audit(f).Count(e => e.GetProperty("action").GetString() == action);

    [Fact]
    public async Task Match_sends_one_email_with_a_link()
    {
        await using var rig = Factory();
        var page = await AskAsync(Client(rig), "Ann Smith", "ann@example.com");

        Assert.Equal(Reply, ReplyOf(page));
        var sent = Assert.Single(await SentAsync(rig, 1));
        Assert.Equal("ann@example.com", sent.To);
        Assert.Equal("Reset your Avalon password", sent.Email.Subject);
        Assert.Matches(@"http://[^/\s]+:8001/guest/reset-password\?token=[A-Za-z0-9_-]{43}", sent.Email.Text);
        Assert.Contains("30 minutes", sent.Email.Text);
        Assert.Equal(1, Actions(rig.F, "reset-requested"));
    }

    [Fact]
    public async Task Link_uses_the_grid_address_not_the_request_host()
    {
        await using var rig = Factory(also: s => s with { Hostname = "grid.example.org" });
        var client = Client(rig);
        client.DefaultRequestHeaders.Host = "evil.example";
        await AskAsync(client, "Ann Smith", "ann@example.com");

        var sent = Assert.Single(await SentAsync(rig, 1));
        Assert.Contains("http://grid.example.org:8001/guest/reset-password?token=", sent.Email.Text);
        Assert.DoesNotContain("evil.example", sent.Email.Text);
    }

    [Fact]
    public async Task Same_reply_for_no_match_wrong_email_and_rule_off_for_that_group()
    {
        await using var rig = Factory(new RecoverySettings(RecoveryRule.EmailLink, RecoveryRule.Off, RecoveryRule.EmailLink));
        var client = Client(rig);

        var nobody = await AskAsync(client, "No Body", "nobody@example.com");
        var wrongEmail = await AskAsync(client, "Ann Smith", "someone-else@example.com");
        var staffOff = await AskAsync(client, "Adam Admin", "adam@example.com");

        Assert.Equal(Reply, ReplyOf(nobody));
        Assert.Equal(Reply, ReplyOf(wrongEmail));
        Assert.Equal(Reply, ReplyOf(staffOff));
        await Task.Delay(200, Ct);
        lock (rig.Transport.Sent) Assert.Empty(rig.Transport.Sent);
        Assert.Equal(3, Actions(rig.F, "reset-refused"));
    }

    [Fact]
    public async Task Email_match_ignores_case_and_spaces()
    {
        await using var rig = Factory();
        await AskAsync(Client(rig), "Ann Smith", "  ANN@Example.com ");
        Assert.Single(await SentAsync(rig, 1));
    }

    [Fact]
    public async Task Staff_account_gets_the_staff_rule_and_an_email()
    {
        await using var rig = Factory(new RecoverySettings(RecoveryRule.Off, RecoveryRule.EmailLink, RecoveryRule.EmailLink));
        await AskAsync(Client(rig), "Adam Admin", "adam@example.com");
        Assert.Equal("adam@example.com", Assert.Single(await SentAsync(rig, 1)).To);
    }

    [Fact]
    public async Task Fourth_request_for_one_account_sends_nothing()
    {
        await using var rig = Factory();
        var client = Client(rig);
        for (var i = 0; i < 4; i++)
            Assert.Equal(Reply, ReplyOf(await AskAsync(client, "Ann Smith", "ann@example.com")));

        Assert.Equal(3, Actions(rig.F, "reset-requested"));
        Assert.Equal(1, Actions(rig.F, "reset-refused"));
    }

    [Fact]
    public async Task Eleventh_request_from_an_address_does_not_look_up()
    {
        await using var rig = Factory();
        var client = Client(rig);
        for (var i = 0; i < ResetLimits.PerAddress; i++) await AskAsync(client, "No Body", "x@example.com");
        var before = rig.F.Robust.Lookups;

        var page = await AskAsync(client, "Ann Smith", "ann@example.com");

        Assert.Equal(Reply, ReplyOf(page));
        Assert.Equal(before, rig.F.Robust.Lookups);
        Assert.Equal(0, Actions(rig.F, "reset-requested"));
    }

    [Fact]
    public async Task All_rules_off_shows_grid_message()
    {
        await using var rig = Factory(new RecoverySettings(RecoveryRule.Off, RecoveryRule.Off, RecoveryRule.Off));
        var page = await PageAsync(Client(rig), "/guest/forgot-password");
        Assert.Equal(GridOff, page.QuerySelector("#reset-off")?.TextContent.Trim());
        Assert.Null(page.QuerySelector("form#forgot"));
    }

    [Fact]
    public async Task No_rules_chosen_shows_grid_message()
    {
        await using var rig = Factory(also: s => s with { Recovery = null });
        var page = await PageAsync(Client(rig), "/guest/forgot-password");
        Assert.Equal(GridOff, page.QuerySelector("#reset-off")?.TextContent.Trim());
    }

    [Fact]
    public async Task Email_not_ready_shows_grid_message()
    {
        await using var rig = Factory(email: false);
        var page = await PageAsync(Client(rig), "/guest/forgot-password");
        Assert.Equal(GridOff, page.QuerySelector("#reset-off")?.TextContent.Trim());
    }

    [Fact]
    public async Task Suspended_account_gets_no_email()
    {
        await using var rig = Factory();
        Assert.Equal(Reply, ReplyOf(await AskAsync(Client(rig), "Sue Spended", "sue@example.com")));
        Assert.Equal(0, Actions(rig.F, "reset-requested"));
    }

    [Fact]
    public async Task Robust_down_still_gives_the_reply()
    {
        await using var rig = Factory();
        var client = Client(rig);
        var page = await PageAsync(client, "/guest/forgot-password");
        rig.F.Robust.Down = true;
        using var r = await client.PostAsync("/guest/forgot-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Ann Smith", ["Email"] = "ann@example.com", ["__RequestVerificationToken"] = Token(page),
        }), Ct);
        Assert.Equal(Reply, ReplyOf(new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct))));
    }

    [Fact]
    public async Task Bot_check_failure_says_so()
    {
        await using var rig = Factory();
        var page = await AskAsync(Client(rig), "Ann Smith", "ann@example.com", honeypot: "http://spam.example");

        Assert.Null(ReplyOf(page));
        Assert.Equal("The form couldn't check that you're a person. Try again.", page.QuerySelector(".error")?.TextContent.Trim());
        Assert.Equal(0, Actions(rig.F, "reset-requested"));
    }

    [Fact]
    public async Task Page_404_on_region_server()
    {
        await using var rig = Factory(plan: OwnerPlan.RegionServer);
        using var r = await Client(rig).GetAsync("/guest/forgot-password", Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Open_from_outside_admin_networks()
    {
        await using var rig = Factory(adminNetworks: ["10.0.0.0/8"]);
        var page = await AskAsync(Client(rig), "Adam Admin", "adam@example.com");
        Assert.Equal(Reply, ReplyOf(page));
        Assert.Single(await SentAsync(rig, 1));
    }

    [Fact]
    public async Task Sign_in_pages_link_to_forgot_password_only_with_guest_side()
    {
        await using (var rig = Factory())
        {
            var client = Client(rig);
            Assert.NotNull((await PageAsync(client, "/login")).QuerySelector("a[href='/guest/forgot-password']"));
            Assert.NotNull((await PageAsync(client, "/guest/sign-in")).QuerySelector("a[href='/guest/forgot-password']"));
        }
        await using (var rig = Factory(plan: OwnerPlan.RegionServer))
        {
            Assert.Null((await PageAsync(Client(rig), "/login")).QuerySelector("a[href='/guest/forgot-password']"));
        }
    }

    [Fact]
    public async Task Audit_has_no_email_or_token()
    {
        await using var rig = Factory();
        await AskAsync(Client(rig), "Ann Smith", "ann@example.com");
        await AskAsync(Client(rig), "Ann Smith", "secret-typed@example.com");
        var sent = Assert.Single(await SentAsync(rig, 1));
        var token = System.Text.RegularExpressions.Regex.Match(sent.Email.Text, "token=([A-Za-z0-9_-]+)").Groups[1].Value;

        var text = string.Join('\n', Audit(rig.F).Select(e => e.GetRawText()));
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain(token, text);
    }
}
