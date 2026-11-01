using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's sign-up switch and its warnings on the Legal page, and sign-ups waiting for email on the People page (spec 2026-10-31-sign-up-design §3).</summary>
public sealed class SignUpOwnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public const string BotsOff = "Every protection is off. Bots can create accounts freely.";
    public const string NoEmail = "Email isn't set up, so anyone can sign up without proving an email address.";

    private static async Task<HttpClient> SignedInAsync(SignUpsTests.Rig rig, string role = "owner")
    {
        var c = rig.F.CreateClient(new() { AllowAutoRedirect = false });
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url) =>
        new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields, string tokenFrom)
    {
        var page = await PageAsync(client, tokenFrom);
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static Task<HttpResponseMessage> SwitchAsync(HttpClient client, bool open) =>
        PostAsync(client, "/security/legal?handler=SignUp", open ? new() { ["SignUpOpen"] = "true" } : [], "/security/legal");

    private static List<string> Audit(LauncherFactory f, string action)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("action").GetString() == action)
            .Select(e => e.GetProperty("target").GetString()!).ToList();
    }

    [Fact]
    public async Task Switch_disabled_while_gate_shut()
    {
        await using var rig = SignUpsTests.Factory(open: false,
            before: f => f.Store.Save(f.Store.LoadOrCreate() with { Legal = new LegalSettings() }));
        var owner = await SignedInAsync(rig);

        Assert.NotNull((await PageAsync(owner, "/security/legal")).QuerySelector("#SignUpOpen[disabled]"));
        using var r = await SwitchAsync(owner, true);

        Assert.False(SignUpRules.IsOpen(rig.F.Store.LoadOrCreate()));
        Assert.NotEqual(true, rig.F.Store.LoadOrCreate().SignUp?.Open);
        Assert.Empty(Audit(rig.F, "signup-switch"));
    }

    [Fact]
    public async Task Switch_saves_and_audits()
    {
        await using var rig = SignUpsTests.Factory(open: false);
        var owner = await SignedInAsync(rig);

        using (var on = await SwitchAsync(owner, true)) Assert.Equal(HttpStatusCode.Redirect, on.StatusCode);
        Assert.True(SignUpRules.IsOpen(rig.F.Store.LoadOrCreate()));
        Assert.NotNull((await PageAsync(owner, "/security/legal")).QuerySelector("#SignUpOpen:checked"));
        using (await SwitchAsync(owner, false)) { }

        Assert.False(SignUpRules.IsOpen(rig.F.Store.LoadOrCreate()));
        Assert.Equal(["on", "off"], Audit(rig.F, "signup-switch"));
    }

    [Fact]
    public async Task Non_owner_admin_cannot_switch()
    {
        await using var rig = SignUpsTests.Factory(open: false);
        var admin = await SignedInAsync(rig, "admin");

        using var r = await admin.GetAsync("/security/legal", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.NotEqual(true, rig.F.Store.LoadOrCreate().SignUp?.Open);
    }

    [Fact]
    public async Task Warns_when_all_bot_layers_off()
    {
        await using var rig = SignUpsTests.Factory(before: f => f.Store.Save(f.Store.LoadOrCreate() with
        {
            Bots = new BotSettings(Honeypot: false, RateLimit: false, ProofOfWork: false, ImageCaptcha: false),
        }));
        var owner = await SignedInAsync(rig);

        Assert.Contains(BotsOff, (await PageAsync(owner, "/security/legal")).Body!.TextContent);
    }

    [Fact]
    public async Task Warns_when_email_not_ready()
    {
        await using (var none = SignUpsTests.Factory(email: false))
            Assert.Contains(NoEmail, (await PageAsync(await SignedInAsync(none), "/security/legal")).Body!.TextContent);
        await using (var ready = SignUpsTests.Factory())
        {
            var text = (await PageAsync(await SignedInAsync(ready), "/security/legal")).Body!.TextContent;
            Assert.DoesNotContain(NoEmail, text);
            Assert.DoesNotContain(BotsOff, text);
        }
        await using (var closed = SignUpsTests.Factory(open: false, email: false))
            Assert.DoesNotContain(NoEmail, (await PageAsync(await SignedInAsync(closed), "/security/legal")).Body!.TextContent);
    }

    private static async Task<SignUpsTests.Rig> WithPendingAsync()
    {
        var rig = SignUpsTests.Factory();
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), null, Ct);
        return rig;
    }

    [Fact]
    public async Task People_shows_waiting_for_email()
    {
        await using var rig = await WithPendingAsync();
        var owner = await SignedInAsync(rig);

        var page = await PageAsync(owner, "/people?name=Bea+Example");

        Assert.Contains("Waiting for email", page.QuerySelector("#person")!.TextContent);
        Assert.DoesNotContain("Suspended", page.QuerySelector("#person h2")!.TextContent);
    }

    [Fact]
    public async Task People_offers_no_unsuspend_for_pending()
    {
        await using var rig = await WithPendingAsync();
        var owner = await SignedInAsync(rig);

        Assert.Empty((await PageAsync(owner, "/people?name=Bea+Example")).QuerySelectorAll("#person form"));
        using var r = await PostAsync(owner, "/people?handler=Unsuspend", new() { ["name"] = "Bea Example" }, "/people?name=Bea+Example");

        Assert.Equal(-1, rig.F.Robust.All.Single(a => a.FirstName == "Bea").UserLevel);
        Assert.True(rig.Pending.Contains(rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId));
    }
}
