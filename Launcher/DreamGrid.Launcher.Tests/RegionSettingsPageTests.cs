using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionSettingsPageTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("70000000-0000-0000-0000-000000000002");
    private static readonly Guid SunsetId = Guid.Parse("70000000-0000-0000-0000-000000000003");

    private static readonly string[] Tabs = ["basics", "options", "map", "physics", "scripts", "permissions", "access", "cpu"];

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        // Home and Beach share one program; Sunset Shore has its own.
        Programs = [new ProgramSettings("Home", 8004, 8011, "ubODE"), new ProgramSettings("Sunset", 8005, 8012)],
        Regions =
        [
            new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
            new RegionSettings("Home", "Beach", BeachId, X: 1001, UdpPort: 8004),
            new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1010, UdpPort: 8005),
        ],
    })
    {
        Before = TestAuth.Apply,
        // Whatever this computer's operating system says, High and Real time are unavailable here.
        ExtraServices = s => s.AddSingleton<Func<CpuPriority, string?>>(
            p => p >= CpuPriority.High ? "Only an administrator can raise it that far." : null),
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    private static string Url(Guid id, string tab) => $"/regions/region/{id}/settings/{tab}";

    private static async Task<IHtmlDocument> GetAsync(HttpClient client, Guid id, string tab) =>
        new HtmlParser().ParseDocument(await client.GetStringAsync(Url(id, tab), Ct));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string tab, params (string, string)[] fields)
    {
        var page = await GetAsync(client, id, tab);
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        return await client.PostAsync($"{Url(id, tab)}?handler={tab}", new FormUrlEncodedContent(form), Ct);
    }

    private static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage r) =>
        new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));

    private static string Notice(IHtmlDocument doc) => doc.QuerySelector(".notice")!.TextContent.Trim();

    [Fact]
    public async Task Each_tab_renders_and_unknown_tab_is_404()
    {
        using var client = await SignedInAsync();

        foreach (var tab in Tabs)
        {
            var doc = await GetAsync(client, HomeId, tab);
            Assert.Equal(8, doc.QuerySelectorAll("ul.nav-tabs a.nav-link").Length);
            Assert.Equal(tab, doc.QuerySelector("ul.nav-tabs a.nav-link.active")!.GetAttribute("href")!.Split('/')[^1]);
        }
        // No tab given is Basics.
        var basics = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{HomeId}/settings", Ct));
        Assert.Contains(HomeId.ToString(), basics.Body!.TextContent);
        Assert.Contains("1000, 1000", basics.Body.TextContent);
        using var unknown = await client.GetAsync(Url(HomeId, "nonsense"), Ct);
        using var noRegion = await client.GetAsync(Url(Guid.NewGuid(), "options"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noRegion.StatusCode);
    }

    [Fact]
    public async Task Settings_need_admin()
    {
        using var anon = _f.CreateClient(new() { AllowAutoRedirect = false });

        using var get = await anon.GetAsync(Url(HomeId, "options"), Ct);
        using var post = await anon.PostAsync($"{Url(HomeId, "options")}?handler=options",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["MaxAgents"] = "50" }), Ct);

        Assert.True(get.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized, get.StatusCode.ToString());
        Assert.False(post.IsSuccessStatusCode);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("Save", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Options_save_calls_controller_and_says_when_it_applies()
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, SunsetId, "options", ("MaxAgents", "50"), ("MaxPrims", ""), ("ClampSize", "true"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Url(SunsetId, "options"), response.Headers.Location!.OriginalString);
        var saved = Assert.Single(_f.Grid.SavedRegionOptions);
        Assert.Equal(SunsetId, saved.Region);
        Assert.Equal(new RegionOptions(MaxAgents: 50, ClampSize: true), saved.Options);
        var doc = await GetAsync(client, SunsetId, "options");
        Assert.Equal("Saved. It applies when Sunset Shore restarts.", Notice(doc));
        Assert.Equal("50", doc.QuerySelector("#MaxAgents")!.GetAttribute("value"));
        Assert.Equal("100", doc.QuerySelector("#MaxAgents")!.GetAttribute("placeholder"));
    }

    [Fact]
    public async Task Access_save_in_shared_program_names_both_regions()
    {
        using var client = await SignedInAsync();

        (await PostAsync(client, HomeId, "access", ("KeepResidentsOut", "true"))).Dispose();

        Assert.Equal("Saved. It applies when Home and Beach restart.", Notice(await GetAsync(client, HomeId, "access")));
        Assert.True(_f.Grid.SavedRegionOptions.Single().Options.KeepResidentsOut);
        Assert.False(_f.Grid.SavedRegionOptions.Single().Options.AnnounceVisitors);
    }

    [Fact]
    public async Task Bad_number_shows_at_its_field_and_saves_nothing()
    {
        using var client = await SignedInAsync();

        using var tooBig = await PostAsync(client, HomeId, "options", ("MaxAgents", "9999"));
        using var text = await PostAsync(client, HomeId, "options", ("MaxPrims", "lots"));

        Assert.Equal(HttpStatusCode.OK, tooBig.StatusCode);
        var doc = await ParseAsync(tooBig);
        Assert.Contains("1 to 500", doc.QuerySelector("[data-error-for=MaxAgents]")!.TextContent);
        Assert.Equal("9999", doc.QuerySelector("#MaxAgents")!.GetAttribute("value"));
        Assert.Contains("whole number", (await ParseAsync(text)).QuerySelector("[data-error-for=MaxPrims]")!.TextContent);
        Assert.Empty(_f.Grid.SavedRegionOptions);
    }

    [Fact]
    public async Task Program_tab_names_the_regions_that_share_it()
    {
        using var client = await SignedInAsync();

        var shared = await GetAsync(client, BeachId, "map");
        var alone = await GetAsync(client, SunsetId, "map");
        var options = await GetAsync(client, BeachId, "options");

        Assert.Contains("Home and Beach share this setting because they run together.", shared.Body!.TextContent);
        Assert.DoesNotContain("share this setting", alone.Body!.TextContent);
        Assert.DoesNotContain("share this setting", options.Body!.TextContent); // options belong to the region
    }

    [Fact]
    public async Task Physics_tab_offers_no_hybrid()
    {
        using var client = await SignedInAsync();

        var doc = await GetAsync(client, SunsetId, "physics");

        var labels = doc.QuerySelectorAll("label.form-check-label").Select(l => l.TextContent.Trim()).ToList();
        Assert.Equal(["Grid default (ubODE)", "ubODE", "BulletSim", "BulletSim in its own thread"], labels);
        Assert.DoesNotContain("hybrid", doc.Body!.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", doc.QuerySelector("input[name=Physics][checked]")!.GetAttribute("value")); // Sunset follows the grid default

        using var response = await PostAsync(client, SunsetId, "physics", ("Physics", "BulletSimThreaded"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("BulletSimThreaded", Assert.Single(_f.Grid.SavedProgramOptions).Physics);
        using var bad = await PostAsync(client, SunsetId, "physics", ("Physics", "Hybrid"));
        Assert.Contains("Pick ubODE", (await ParseAsync(bad)).QuerySelector("[data-error-for=Physics]")!.TextContent);
        Assert.Single(_f.Grid.SavedProgramOptions);
    }

    [Fact]
    public async Task Scripts_tab_saves_a_choice_and_rates()
    {
        using var client = await SignedInAsync();

        (await PostAsync(client, SunsetId, "scripts", ("Scripts", "off"), ("Timer", "0.5"), ("Fps", ""))).Dispose();
        using var bad = await PostAsync(client, SunsetId, "scripts", ("Scripts", "on"), ("Timer", "99"), ("Fps", "2"));

        var saved = Assert.Single(_f.Grid.SavedProgramOptions);
        Assert.Equal(new ProgramOptions(Scripts: false, MinTimerInterval: 0.5), saved.Options);
        var doc = await ParseAsync(bad);
        Assert.Contains("0.05 to 10", doc.QuerySelector("[data-error-for=Timer]")!.TextContent);
        Assert.Contains("5 to 55", doc.QuerySelector("[data-error-for=Fps]")!.TextContent);
    }

    [Fact]
    public async Task Cpu_save_on_running_program_says_applied()
    {
        _f.Grid.CpuAppliedNow = true;
        using var client = await SignedInAsync();

        (await PostAsync(client, SunsetId, "cpu", ("Cores", "0"), ("Priority", "AboveNormal"))).Dispose();

        var saved = Assert.Single(_f.Grid.SavedProgramOptions);
        Assert.Equal([0], saved.Options.Cores);
        Assert.Equal(CpuPriority.AboveNormal, saved.Options.Priority);
        var doc = await GetAsync(client, SunsetId, "cpu");
        Assert.Equal("Saved and applied.", Notice(doc));
        Assert.True(doc.QuerySelector("#Core-0")!.HasAttribute("checked"));
        Assert.Equal(Environment.ProcessorCount, doc.QuerySelectorAll("input[name=Cores]").Length);
        Assert.Equal("Core 0", doc.QuerySelector("label[for=Core-0]")!.TextContent.Trim());
    }

    [Fact]
    public async Task Cpu_that_could_not_be_applied_says_why_and_when_it_will()
    {
        _f.Grid.OnRegionSettings = (_, _) => throw new InvalidOperationException("It was refused.");
        using var client = await SignedInAsync();

        (await PostAsync(client, HomeId, "cpu", ("Priority", "Normal"))).Dispose();

        _f.Grid.OnRegionSettings = (_, _) => Task.CompletedTask;
        Assert.Equal("Saved, but it couldn't be applied now: It was refused. It applies when Home and Beach restart.",
            Notice(await GetAsync(client, HomeId, "cpu")));
    }

    [Fact]
    public async Task Cpu_with_a_core_that_does_not_exist_shows_at_the_field()
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, SunsetId, "cpu", ("Cores", "999"));

        Assert.Contains("This computer has cores", (await ParseAsync(response)).QuerySelector("[data-error-for=Cores]")!.TextContent);
        Assert.Empty(_f.Grid.SavedProgramOptions);
    }

    [Fact]
    public async Task Unavailable_priority_is_disabled_with_reason()
    {
        using var client = await SignedInAsync();

        var doc = await GetAsync(client, SunsetId, "cpu");

        Assert.True(doc.QuerySelector("#Priority-High")!.HasAttribute("disabled"));
        Assert.True(doc.QuerySelector("#Priority-RealTime")!.HasAttribute("disabled"));
        Assert.False(doc.QuerySelector("#Priority-AboveNormal")!.HasAttribute("disabled"));
        Assert.Contains("Only an administrator can raise it that far.", doc.QuerySelector("#Priority-High")!.ParentElement!.TextContent);
        Assert.Contains("slow", doc.QuerySelector("#Priority-High")!.ParentElement!.TextContent);
        using var forged = await PostAsync(client, SunsetId, "cpu", ("Priority", "High"));
        Assert.Contains("Only an administrator", (await ParseAsync(forged)).QuerySelector("[data-error-for=Priority]")!.TextContent);
        Assert.Empty(_f.Grid.SavedProgramOptions);
    }

    [Fact]
    public async Task Pages_work_without_javascript_and_never_say_program()
    {
        using var client = await SignedInAsync();

        foreach (var tab in Tabs)
        {
            var doc = await GetAsync(client, HomeId, tab);
            Assert.Empty(doc.QuerySelectorAll("main script"));
            var text = doc.Body!.TextContent.ToLowerInvariant();
            Assert.DoesNotContain("program", text);
            Assert.DoesNotContain("instance", text);
            foreach (var form in doc.QuerySelectorAll("form[action*=settings]"))
                Assert.Equal("post", form.GetAttribute("method"));
        }
    }
}
