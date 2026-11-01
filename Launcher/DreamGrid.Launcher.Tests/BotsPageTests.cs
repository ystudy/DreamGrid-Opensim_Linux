using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's bot-protection page at /security/bots, its Try it form and the challenge endpoint.</summary>
public sealed class BotsPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AllOffWarning = "Every protection is off. Once sign-ups are open, bots can create accounts freely.";

    private static LauncherFactory Factory(OwnerPlan? plan = null, Func<BotSettings, BotSettings>? bots = null)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = plan,
            Bots = bots is null ? s.Bots : bots(s.Bots),
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "owner")
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, Dictionary<string, string> fields, string tokenFrom = "/security/bots")
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync(tokenFrom, Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync("/security/bots?handler=" + handler, new FormUrlEncodedContent(form), Ct);
    }

    /// <summary>Posts the save form with these layers on (an unticked switch posts nothing).</summary>
    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, bool honeypot = false, bool rateLimit = false, int perHour = 5,
        bool proof = false, int difficulty = 50_000, bool image = false, bool approval = false)
    {
        var fields = new Dictionary<string, string> { ["PerHourPerIp"] = perHour.ToString(), ["Difficulty"] = difficulty.ToString() };
        foreach (var (name, on) in new[] { ("Honeypot", honeypot), ("RateLimit", rateLimit), ("ProofOfWork", proof), ("ImageCaptcha", image), ("AdminApproval", approval) })
            if (on) fields[name] = "true";
        return PostAsync(client, "Save", fields);
    }

    private static List<JsonElement> BotAudit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(e => e.GetProperty("action").GetString()!.StartsWith("bot-", StringComparison.Ordinal)).ToList();
    }

    private static string TextOf(string html) => new HtmlParser().ParseDocument(html).Body!.TextContent;

    /// <summary>Asks the endpoint for a challenge and solves it like the browser does; the payload for the pow field.</summary>
    private static async Task<string> SolvedAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/bots/challenge", Ct));
        var r = doc.RootElement;
        var salt = r.GetProperty("salt").GetString()!;
        var challenge = r.GetProperty("challenge").GetString()!;
        var number = ProofOfWorkTests.Solve(salt, challenge, r.GetProperty("maxnumber").GetInt32());
        return ProofOfWorkTests.Payload(r.GetProperty("algorithm").GetString()!, challenge, number, salt, r.GetProperty("signature").GetString()!);
    }

    private static async Task<string?> TryAsync(HttpClient owner, string? website, string? pow)
    {
        var fields = new Dictionary<string, string>();
        if (website is not null) fields["website"] = website;
        if (pow is not null) fields["pow"] = pow;
        using var r = await PostAsync(owner, "Try", fields);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("#try-result")?.TextContent;
    }

    [Fact]
    public async Task Try_form_passes_with_solved_pow_and_stops_honeypot()
    {
        await using var f = Factory(OwnerPlan.Local);
        var owner = await SignedInAsync(f);

        // The form carries the fields of the layers that are on.
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/bots", Ct));
        var form = page.QuerySelector("#try-form")!;
        var trap = form.QuerySelector(".dg-hp input[name=website]")!;
        Assert.Equal(("off", "-1"), (trap.GetAttribute("autocomplete"), trap.GetAttribute("tabindex")));
        Assert.Equal("Leave this empty", form.QuerySelector(".dg-hp label")!.TextContent);
        var pow = form.QuerySelector("input[name=pow]")!;
        Assert.Equal("/api/bots/challenge", pow.GetAttribute("data-challenge-url"));
        Assert.NotNull(form.QuerySelector("script[src='/js/pow.js'][defer]"));
        Assert.Null(page.QuerySelector("#try-result"));

        Assert.Equal("Passed", await TryAsync(owner, "", await SolvedAsync(owner)));
        Assert.Equal("Stopped by Honeypot", await TryAsync(owner, "http://spam.example", await SolvedAsync(owner)));
        Assert.Equal("Stopped by Proof of work", await TryAsync(owner, "", null));
        // The same answer twice.
        var once = await SolvedAsync(owner);
        Assert.Equal("Passed", await TryAsync(owner, "", once));
        Assert.Equal("Stopped by Proof of work", await TryAsync(owner, "", once));
    }

    [Fact]
    public async Task Try_form_reports_the_rate_limit_and_shows_only_the_layers_that_are_on()
    {
        await using var f = Factory(OwnerPlan.Local, b => b with { Honeypot = false, ProofOfWork = false, PerHourPerIp = 2 });
        var owner = await SignedInAsync(f);

        var form = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/bots", Ct)).QuerySelector("#try-form")!;
        Assert.Null(form.QuerySelector("input[name=website]"));
        Assert.Null(form.QuerySelector("input[name=pow]"));
        Assert.Null(form.QuerySelector("script"));

        Assert.Equal("Passed", await TryAsync(owner, null, null));
        Assert.Equal("Passed", await TryAsync(owner, null, null));
        Assert.Equal("Stopped by Rate limit", await TryAsync(owner, null, null));
    }

    [Fact]
    public async Task Try_form_shows_the_picture_and_its_question_when_the_image_check_is_on()
    {
        await using var f = Factory(OwnerPlan.Local, b => b with { Honeypot = false, ProofOfWork = false, RateLimit = false, ImageCaptcha = true });
        var owner = await SignedInAsync(f);

        var form = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/bots", Ct)).QuerySelector("#try-form")!;
        var image = form.QuerySelector("img")!;
        Assert.Equal("Picture of letters; answer the question below instead if you can't see it", image.GetAttribute("alt"));
        var src = image.GetAttribute("src")!;
        Assert.StartsWith("data:image/svg+xml;base64,", src);
        var svg = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(src["data:image/svg+xml;base64,".Length..]));
        Assert.DoesNotContain("<text", svg);
        Assert.Equal("Type the letters", form.QuerySelector("label[for=dg-captcha-letters]")!.TextContent);
        Assert.NotNull(form.QuerySelector("input#dg-captcha-letters[name=captcha-letters]"));
        Assert.NotNull(form.QuerySelector("input#dg-captcha-answer[name=captcha-answer]"));
        Assert.Matches(@"^What is \w+ plus \w+\?$", form.QuerySelector("label[for=dg-captcha-answer]")!.TextContent);
        Assert.NotEmpty(form.QuerySelector("input[type=hidden][name=captcha-token]")!.GetAttribute("value")!);
        Assert.Null(form.QuerySelector("input[name=website]"));
    }

    [Fact]
    public async Task Try_form_passes_the_image_check_with_the_sum_and_uses_each_picture_once()
    {
        await using var f = Factory(OwnerPlan.Local, b => b with { Honeypot = false, ProofOfWork = false, RateLimit = false, ImageCaptcha = true });
        var owner = await SignedInAsync(f);

        // Reads the form the way a visitor would: the question is the only part of the answer the page gives away.
        async Task<(string Token, string Sum)> NextAsync()
        {
            var form = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/bots", Ct)).QuerySelector("#try-form")!;
            var words = new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
            var question = Regex.Match(form.QuerySelector("label[for=dg-captcha-answer]")!.TextContent, @"What is (\w+) plus (\w+)\?");
            var sum = Array.IndexOf(words, question.Groups[1].Value) + Array.IndexOf(words, question.Groups[2].Value);
            return (form.QuerySelector("input[name=captcha-token]")!.GetAttribute("value")!, sum.ToString());
        }

        async Task<string?> TryAsync(string? token, string? letters, string? answer)
        {
            var fields = new Dictionary<string, string>();
            if (token is not null) fields["captcha-token"] = token;
            if (letters is not null) fields["captcha-letters"] = letters;
            if (answer is not null) fields["captcha-answer"] = answer;
            using var r = await PostAsync(owner, "Try", fields);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("#try-result")?.TextContent;
        }

        var (token, sum) = await NextAsync();
        Assert.Equal("Stopped by Image check", await TryAsync(null, null, null));
        Assert.Equal("Stopped by Image check", await TryAsync(token, "AAAAA", "99"));
        // The token was spent by the wrong try above.
        Assert.Equal("Stopped by Image check", await TryAsync(token, null, sum));

        var (fresh, freshSum) = await NextAsync();
        Assert.Equal("Passed", await TryAsync(fresh, "", freshSum));
        Assert.Equal("Stopped by Image check", await TryAsync(fresh, "", freshSum));
    }

    [Fact]
    public async Task Owner_saves_layers()
    {
        await using var f = Factory(OwnerPlan.Local);
        var owner = await SignedInAsync(f);

        using var r = await SaveAsync(owner, honeypot: false, rateLimit: true, perHour: 8, proof: true, difficulty: 2000, image: true, approval: true);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/security/bots", r.Headers.Location?.OriginalString);
        Assert.Equal(new BotSettings(false, true, 8, true, 2000, true, false), f.Store.LoadOrCreate().Bots);
        Assert.Equal([false], f.Grid.UpdateWriteConfig);

        var html = await owner.GetStringAsync("/security/bots", Ct);
        Assert.Contains("Saved. The protection is live now.", TextOf(html));
        var page = new HtmlParser().ParseDocument(html);
        Assert.Null(page.QuerySelector("#Honeypot:checked"));
        Assert.NotNull(page.QuerySelector("#RateLimit:checked"));
        Assert.Equal("8", page.QuerySelector("#PerHourPerIp")!.GetAttribute("value"));
        Assert.Equal("2000", page.QuerySelector("#Difficulty")!.GetAttribute("value"));
        Assert.NotNull(page.QuerySelector("#ImageCaptcha:checked"));
        // Admin approval isn't built yet: the switch is hidden and a posted value is ignored.
        Assert.Null(page.QuerySelector("#AdminApproval"));
        // The challenge now has the new difficulty.
        using var challenge = JsonDocument.Parse(await f.CreateClient().GetStringAsync("/api/bots/challenge", Ct));
        Assert.Equal(2000, challenge.RootElement.GetProperty("maxnumber").GetInt32());
    }

    [Fact]
    public async Task Saving_the_same_again_changes_nothing_and_is_not_audited()
    {
        await using var f = Factory(OwnerPlan.Local);
        var owner = await SignedInAsync(f);

        using var r = await SaveAsync(owner, honeypot: true, rateLimit: true, perHour: 5, proof: true, difficulty: 50_000);

        Assert.Equal(new BotSettings(), f.Store.LoadOrCreate().Bots);
        Assert.Empty(BotAudit(f));
        Assert.Contains("Nothing changed.", TextOf(await owner.GetStringAsync("/security/bots", Ct)));
    }

    [Fact]
    public async Task Turning_off_is_audited()
    {
        await using var f = Factory(OwnerPlan.Local);
        var owner = await SignedInAsync(f);

        // Honeypot and the rate limit off; proof of work stays on, image CAPTCHA on.
        using var off = await SaveAsync(owner, proof: true, image: true);
        // Honeypot back on.
        using var on = await SaveAsync(owner, honeypot: true, proof: true, image: true);

        var entries = BotAudit(f).Select(e => (e.GetProperty("action").GetString(), e.GetProperty("target").GetString(),
            e.GetProperty("actor").GetString(), e.GetProperty("role").GetString(), e.GetProperty("result").GetString())).ToList();
        Assert.Equal(
        [
            ("bot-layer-off", "honeypot", "Test Owner", "owner", "ok"),
            ("bot-layer-off", "rate-limit", "Test Owner", "owner", "ok"),
            ("bot-layer-on", "image-captcha", "Test Owner", "owner", "ok"),
            ("bot-layer-on", "honeypot", "Test Owner", "owner", "ok"),
        ], entries);
    }

    [Fact]
    public async Task A_changed_number_is_audited_as_a_setting()
    {
        await using var f = Factory(OwnerPlan.Local);
        var owner = await SignedInAsync(f);

        using var r = await SaveAsync(owner, honeypot: true, rateLimit: true, perHour: 7, proof: true, difficulty: 3000, approval: true);

        Assert.Equal(["bot-settings:rate-limit 7 per hour", "bot-settings:difficulty 3000"],
            BotAudit(f).Select(e => e.GetProperty("action").GetString() + ":" + e.GetProperty("target").GetString()));
    }

    [Fact]
    public async Task Bots_post_keeps_stored_admin_approval()
    {
        await using var f = Factory(OwnerPlan.Local);
        f.Store.Save(f.Store.LoadOrCreate() with { Bots = new BotSettings(AdminApproval: true) });
        var owner = await SignedInAsync(f);

        using var r = await SaveAsync(owner, honeypot: true, rateLimit: true, proof: true);

        Assert.True(f.Store.LoadOrCreate().Bots.AdminApproval);
        Assert.Null(new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/bots", Ct)).QuerySelector("#AdminApproval"));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1_000_001)]
    public async Task A_difficulty_out_of_range_is_refused(int difficulty)
    {
        await using var f = Factory(OwnerPlan.Local);
        var owner = await SignedInAsync(f);

        using var r = await SaveAsync(owner, honeypot: true, rateLimit: true, proof: true, difficulty: difficulty);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Use a difficulty from 1000 to 1000000.", TextOf(await r.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(new BotSettings(), f.Store.LoadOrCreate().Bots);
        Assert.Empty(BotAudit(f));
    }

    [Fact]
    public async Task All_off_warns()
    {
        await using var f = Factory(OwnerPlan.Local);
        var owner = await SignedInAsync(f);
        Assert.DoesNotContain(AllOffWarning, TextOf(await owner.GetStringAsync("/security/bots", Ct)));

        using var off = await SaveAsync(owner);

        Assert.Contains(AllOffWarning, TextOf(await owner.GetStringAsync("/security/bots", Ct)));
        using var one = await SaveAsync(owner, image: true);
        Assert.DoesNotContain(AllOffWarning, TextOf(await owner.GetStringAsync("/security/bots", Ct)));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("supervisor")]
    public async Task Admin_gets_403(string role)
    {
        await using var f = Factory(OwnerPlan.Local);
        var client = await SignedInAsync(f, role);

        using var get = await client.GetAsync("/security/bots", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Theory]
    [InlineData(OwnerPlan.Connected)]
    [InlineData(OwnerPlan.RegionServer)]
    public async Task Bots_page_404_on_region_server(OwnerPlan plan)
    {
        await using var f = Factory(plan);
        var owner = await SignedInAsync(f);

        using var get = await owner.GetAsync("/security/bots", Ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        // Nothing to take a token from here either, so the Security page gives it.
        using var post = await PostAsync(owner, "Save", new() { ["Difficulty"] = "2000", ["PerHourPerIp"] = "5" }, tokenFrom: "/security");
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(new BotSettings(), f.Store.LoadOrCreate().Bots);
        using var challenge = await f.CreateClient().GetAsync("/api/bots/challenge", Ct);
        Assert.Equal(HttpStatusCode.NotFound, challenge.StatusCode);

        // The Security page stays, without the link.
        var security = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security", Ct));
        Assert.Null(security.QuerySelector("#bots-link"));
    }

    [Fact]
    public async Task The_security_page_links_to_the_bots_page()
    {
        await using var f = Factory(OwnerPlan.GridAsset);
        var owner = await SignedInAsync(f);

        var security = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security", Ct));

        Assert.Equal("/security/bots", security.QuerySelector("#bots-link")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Challenge_endpoint_anonymous_and_uses_difficulty()
    {
        await using var f = Factory(OwnerPlan.Local, b => b with { Difficulty = 1000 });
        var anonymous = f.CreateClient(new() { AllowAutoRedirect = false });

        using var r = await anonymous.GetAsync("/api/bots/challenge", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct));
        var c = doc.RootElement;
        Assert.Equal("SHA-256", c.GetProperty("algorithm").GetString());
        Assert.Equal(1000, c.GetProperty("maxnumber").GetInt32());
        Assert.Matches(@"^[0-9a-f]{24}\?expires=\d+$", c.GetProperty("salt").GetString());
        Assert.Matches("^[0-9a-f]{64}$", c.GetProperty("challenge").GetString());
        Assert.Matches("^[0-9a-f]{64}$", c.GetProperty("signature").GetString());

        // The signature is made with the key kept in the data directory, so the same key checks the answer.
        var key = ProofOfWork.LoadOrCreateKey(Path.Combine(f.Paths.Security, "pow.key"));
        var work = new ProofOfWork(key, f.Time);
        var salt = c.GetProperty("salt").GetString()!;
        var challenge = c.GetProperty("challenge").GetString()!;
        var number = ProofOfWorkTests.Solve(salt, challenge, 1000);
        Assert.True(work.Verify(ProofOfWorkTests.Payload("SHA-256", challenge, number, salt, c.GetProperty("signature").GetString()!)));
    }
}
