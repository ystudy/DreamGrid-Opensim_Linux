using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The sign-in and account pages (plan 16a, task 5) take every word from <c>ResidentText</c>: rendered in English, no page shows a
/// raw key (a key with no English entry renders as its own name), and every key the group's source files use has an entry.
/// </summary>
public sealed class SignInPagesKeysTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex RawKey = new(@"[A-Z][a-zA-Z]+\.[A-Z][a-zA-Z]+", RegexOptions.Compiled);

    private static readonly Guid Hank = Guid.Parse("0c000000-0000-0000-0000-00000000000c");

    private static LauncherFactory Factory()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Recovery = new RecoverySettings(RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode),
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Gail", "Guest", 50, "pass-word1", Guid.Parse("0b000000-0000-0000-0000-00000000000b"));
        f.Robust.Add("Hank", "Hole", 50, "pass-word1", Hank);
        return f;
    }

    private static HttpClient Client(LauncherFactory f) =>
        f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("http://localhost") });

    private static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage r) =>
        new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));

    private static async Task<IHtmlDocument> GetAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await ParseAsync(r);
    }

    private static string Token(IHtmlDocument page) =>
        page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value") ?? throw new InvalidOperationException("no token");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> form)
    {
        var page = await GetAsync(client, url);
        form["__RequestVerificationToken"] = Token(page);
        return await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    /// <summary>No raw key in the title, the visible text of main, or its placeholder, aria-label, title and alt words.</summary>
    private static void NoRawKeys(IHtmlDocument page, string what)
    {
        var main = page.QuerySelector("main") ?? throw new InvalidOperationException(what + ": no <main>");
        Assert.False(string.IsNullOrWhiteSpace(main.TextContent), what + ": main is empty");
        var words = new List<string> { page.Title ?? "", main.TextContent };
        foreach (var e in main.QuerySelectorAll("*"))
            foreach (var a in new[] { "placeholder", "aria-label", "title", "alt" })
                if (e.GetAttribute(a) is { } v) words.Add(v);
        foreach (var w in words)
            Assert.False(RawKey.IsMatch(w), $"{what}: '{RawKey.Match(w).Value}' looks like a key with no English text (in '{w.Trim()}')");
    }

    [Fact]
    public async Task ShowsEveryKeyItUses()
    {
        await using var f = Factory();
        var anon = Client(f);

        NoRawKeys(await GetAsync(anon, "/guest/sign-in"), "sign-in");

        using (var wrong = await PostAsync(anon, "/guest/sign-in", new() { ["Name"] = "Gail Guest", ["Password"] = "nope-nope" }))
        {
            var page = await ParseAsync(wrong);
            Assert.NotNull(page.QuerySelector(".error"));
            NoRawKeys(page, "sign-in with a wrong password");
        }

        NoRawKeys(await GetAsync(anon, "/guest/forgot-password"), "forgot-password");
        NoRawKeys(await GetAsync(anon, "/guest/reset-password?token=nothing"), "reset-password, an unknown link");
        NoRawKeys(await GetAsync(anon, "/guest/confirm?token=nothing"), "confirm");
        NoRawKeys(await GetAsync(anon, "/guest/confirm-email?token=nothing"), "confirm-email");
        NoRawKeys(await GetAsync(anon, "/guest/connect"), "connect");

        // A resident with two-factor on: the code step, its wrong-code message, and the two-factor page in its "on" state.
        var store = f.Services.GetRequiredService<ResidentTwoFactor>().Store;
        var (secret, _) = store.BeginEnrol(Hank, "Hank Hole", "Avalon");
        Assert.NotNull(store.ConfirmEnrol(Hank, Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow())), secret));
        f.Time.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds));

        var hank = Client(f);
        using (var step = await PostAsync(hank, "/guest/sign-in", new() { ["Name"] = "Hank Hole", ["Password"] = "pass-word1" }))
            Assert.Equal("/guest/sign-in/code", step.Headers.Location?.OriginalString);
        NoRawKeys(await GetAsync(hank, "/guest/sign-in/code"), "sign-in code");
        using (var bad = await PostAsync(hank, "/guest/sign-in/code", new() { ["Code"] = "not-a-code" }))
        {
            var page = await ParseAsync(bad);
            Assert.NotNull(page.QuerySelector(".error"));
            NoRawKeys(page, "sign-in code, a wrong code");
        }

        // A resident without two-factor (the rule asks for it): the setup state with its QR code.
        var gail = Client(f);
        using (var ok = await PostAsync(gail, "/guest/sign-in", new() { ["Name"] = "Gail Guest", ["Password"] = "pass-word1" }))
            Assert.Equal("/guest", ok.Headers.Location?.OriginalString);
        var setup = await GetAsync(gail, "/guest/two-factor");
        Assert.NotNull(setup.QuerySelector("#turn-on"));
        NoRawKeys(setup, "two-factor, set up");
    }

    [Fact]
    public async Task EveryKeyTheGroupUsesHasAnEnglishEntry()
    {
        await using var f = Factory();
        var t = f.Services.GetRequiredService<IStringLocalizer<ResidentText>>();
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "GridGate.Launcher", "Pages", "Guest", "SignIn.cshtml")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        Assert.NotNull(root);
        var guest = Path.Combine(root, "GridGate.Launcher", "Pages", "Guest");
        var files = new[] { "SignIn", "SignInCode", "TwoFactor", "ForgotPassword", "ResetPassword", "Confirm", "ConfirmEmail", "SignOut", "Connect" }
            .SelectMany(p => new[] { Path.Combine(guest, p + ".cshtml"), Path.Combine(guest, p + ".cshtml.cs") })
            .Append(Path.Combine(root, "GridGate.Launcher", "Pages", "Shared", "_LoginAddress.cshtml"))
            .Append(Path.Combine(root, "GridGate.Launcher", "Languages", "PasswordFaultText.cs"));

        var keys = files.Where(File.Exists)
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"(?:\b[tT]\[|\bnew\()""([A-Z][A-Za-z]+\.[A-Za-z0-9]+)""").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.True(keys.Count > 80, "found only " + keys.Count + " keys; the scan is not finding them");
        Assert.All(keys, k => Assert.False(t[k].ResourceNotFound, k + " has no entry in ResidentText.resx"));
    }
}
