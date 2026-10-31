using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The guest accepts the current Terms before anything else; a new version asks again. All texts are made up.</summary>
public sealed class TermsAcceptanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Saved = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static LegalSettings TermsText(string text, int version) => new(
        new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = new Dictionary<string, LegalText> { [LegalPages.Terms] = new(text, version, Saved) },
        });

    private static LauncherFactory Factory(LegalSettings? legal = null, Action<string>? beforeStart = null)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Legal = legal ?? new LegalSettings(),
        })
        {
            // The address the acceptance is recorded from.
            Before = app =>
            {
                app.Use(async (ctx, next) =>
                {
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
                    await next(ctx);
                });
                TestAuth.Apply(app);
            },
        };
        TestAuth.AddStaff(f.Robust);
        beforeStart?.Invoke(f.Paths.Guests);
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

    private static string? Location(HttpResponseMessage r) => r.Headers.Location?.OriginalString;

    private static TermsAcceptances Store(LauncherFactory f) => f.Services.GetRequiredService<TermsAcceptances>();

    private static void SetTerms(LauncherFactory f, string text, int version) =>
        f.Store.Save(f.Store.LoadOrCreate() with { Legal = TermsText(text, version) });

    private static async Task<string> TokenAsync(HttpClient guest)
    {
        var page = new HtmlParser().ParseDocument(await guest.GetStringAsync("/guest/terms", Ct));
        return page.QuerySelector("form[action='/guest/terms'] input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
    }

    /// <summary>Posts the Terms form: the token from the page, the version the form is made to name.</summary>
    private static async Task<HttpResponseMessage> PostAcceptAsync(HttpClient guest, string version, string? returnUrl = null, string? token = null)
    {
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token ?? await TokenAsync(guest), ["Version"] = version };
        if (returnUrl is not null) form["ReturnUrl"] = returnUrl;
        return await guest.PostAsync("/guest/terms", new FormUrlEncodedContent(form), Ct);
    }

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public async Task Guest_without_acceptance_is_sent_to_terms()
    {
        await using var f = Factory(TermsText("Test terms one.", 1));
        var guest = await GuestAsync(f);

        using var home = await guest.GetAsync("/guest", Ct);

        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/guest/terms?ReturnUrl=%2Fguest", Location(home));

        using var terms = await guest.GetAsync("/guest/terms", Ct);
        Assert.Equal(HttpStatusCode.OK, terms.StatusCode);
        var page = new HtmlParser().ParseDocument(await terms.Content.ReadAsStringAsync(Ct));
        var main = page.QuerySelector("main")!.TextContent;
        Assert.Contains("Avalon", main);
        Assert.Contains("Test terms one.", main);
        Assert.Contains("Version 1", main);
        Assert.Equal("I accept these terms", page.QuerySelector("form[action='/guest/terms'] button")!.TextContent.Trim());
        Assert.Equal("Terms - Avalon", page.Title);
    }

    [Fact]
    public async Task Accepting_records_principal_version_time_ip()
    {
        await using var f = Factory(TermsText("Test terms one.", 1));
        var guest = await GuestAsync(f);
        f.Time.Advance(TimeSpan.FromMinutes(5));
        var now = f.Time.GetUtcNow();

        using var r = await PostAcceptAsync(guest, "1", "/guest");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest", Location(r));
        var accepted = Assert.Single(Store(f).Of(TestAuth.GuestId));
        Assert.Equal(new TermsAcceptance(TestAuth.GuestId, 1, now, "203.0.113.9"), accepted);
        Assert.True(Store(f).HasAccepted(TestAuth.GuestId, 1));
        Assert.False(Store(f).HasAccepted(TestAuth.GuestId, 2));
        Assert.False(Store(f).HasAccepted(Guid.NewGuid(), 1));
        // It is on disk, and the pages open now.
        Assert.Contains(TestAuth.GuestId.ToString(), File.ReadAllText(Path.Combine(f.Paths.Guests, "terms.json")));
        using var home = await guest.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        // And audited.
        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "terms-accepted");
        Assert.Equal("guest", entry.GetProperty("role").GetString());
        Assert.Equal("Test Guest", entry.GetProperty("actor").GetString());
        Assert.Equal(TestAuth.GuestId, entry.GetProperty("actorId").GetGuid());
        Assert.Equal("terms v1", entry.GetProperty("target").GetString());
        Assert.Equal("ok", entry.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Accepting_goes_back_to_the_page_asked_for_and_never_off_site()
    {
        await using var f = Factory(TermsText("Test terms one.", 1));
        var guest = await GuestAsync(f);

        var token = await TokenAsync(guest);

        using var off = await PostAcceptAsync(guest, "1", "https://example.org/phish", token);
        Assert.Equal("/guest", Location(off));

        using var local = await PostAcceptAsync(guest, "1", "/guest?x=1", token);
        Assert.Equal("/guest?x=1", Location(local));
        // Accepting again records nothing more.
        Assert.Single(Store(f).Of(TestAuth.GuestId));
    }

    [Fact]
    public async Task New_version_asks_again()
    {
        await using var f = Factory(TermsText("Test terms one.", 1));
        var guest = await GuestAsync(f);
        (await PostAcceptAsync(guest, "1")).Dispose();
        using (var ok = await guest.GetAsync("/guest", Ct)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        SetTerms(f, "Test terms two.", 2);

        using var again = await guest.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.StartsWith("/guest/terms", Location(again));
        (await PostAcceptAsync(guest, "2")).Dispose();
        // Both are kept, newest last.
        Assert.Equal([1, 2], Store(f).Of(TestAuth.GuestId).Select(a => a.Version));
        using var home = await guest.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task Stale_version_post_refused()
    {
        await using var f = Factory(TermsText("Test terms one.", 1));
        var guest = await GuestAsync(f);
        // The form shows version 1, and the owner saves version 2 before it is posted.
        var token = await TokenAsync(guest);
        SetTerms(f, "Test terms two.", 2);

        using var r = await guest.PostAsync("/guest/terms", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Version"] = "1",
        }), Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var shown = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        var main = shown.QuerySelector("main")!.TextContent;
        Assert.Contains("The terms changed while you were reading. Read the new version.", main);
        Assert.Contains("Test terms two.", main);
        Assert.Contains("Version 2", main);
        Assert.Empty(Store(f).Of(TestAuth.GuestId));
        Assert.DoesNotContain(Audit(f), e => e.GetProperty("action").GetString() == "terms-accepted");
    }

    [Fact]
    public async Task No_terms_text_no_gate()
    {
        await using var f = Factory();
        var guest = await GuestAsync(f);

        using var home = await guest.GetAsync("/guest", Ct);

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        // The Terms page has nothing to accept, so it sends the guest on.
        using var terms = await guest.GetAsync("/guest/terms?ReturnUrl=%2Fguest", Ct);
        Assert.Equal(HttpStatusCode.Redirect, terms.StatusCode);
        Assert.Equal("/guest", Location(terms));
    }

    [Fact]
    public async Task Cleared_terms_no_gate()
    {
        await using var f = Factory(TermsText("Test terms one.", 1));
        var guest = await GuestAsync(f);
        SetTerms(f, "", 2);

        using var home = await guest.GetAsync("/guest", Ct);

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task Damaged_file_asks_everyone_again()
    {
        // The file is garbage before the host starts: nobody has accepted.
        await using var f = Factory(TermsText("Test terms one.", 1), guests =>
        {
            Directory.CreateDirectory(guests);
            File.WriteAllText(Path.Combine(guests, "terms.json"), "this is {not json");
        });
        var guest = await GuestAsync(f);

        using var home = await guest.GetAsync("/guest", Ct);

        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.StartsWith("/guest/terms", Location(home));
        Assert.Empty(Store(f).Of(TestAuth.GuestId));
        // The damaged file was put aside, not overwritten; accepting works again.
        Assert.Contains(Directory.GetFiles(f.Paths.Guests), p => Path.GetFileName(p).StartsWith("terms.json.damaged-", StringComparison.Ordinal));
        (await PostAcceptAsync(guest, "1")).Dispose();
        using var after = await guest.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Acceptances_survive_a_restart()
    {
        await using var f = Factory(TermsText("Test terms one.", 1));
        var guest = await GuestAsync(f);
        (await PostAcceptAsync(guest, "1")).Dispose();

        var reloaded = new TermsAcceptances(Path.Combine(f.Paths.Guests, "terms.json"), f.Time);

        Assert.True(reloaded.HasAccepted(TestAuth.GuestId, 1));
        Assert.False(reloaded.HasAccepted(TestAuth.GuestId, 2));
    }

    [Fact]
    public async Task Sign_out_reachable_before_accepting()
    {
        await using var f = Factory(TermsText("Test terms one.", 1));
        var guest = await GuestAsync(f);
        // The sign-out form is on the guest home, which is behind the gate; the Terms page is not, and the token works on both.
        var token = await TokenAsync(guest);

        using var r = await guest.PostAsync("/guest/sign-out", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", Location(r));
        using var home = await guest.GetAsync("/guest", Ct);
        Assert.Equal("/guest/sign-in", home.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task Sign_in_page_is_not_gated()
    {
        await using var f = Factory(TermsText("Test terms one.", 1));
        var anonymous = Client(f);

        using var r = await anonymous.GetAsync("/guest/sign-in", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
