using System.Net;
using System.Runtime.InteropServices;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// Settings moved from a Linux computer to a Mac still say Rapier. Nothing is rewritten behind the user's back, so the
/// pages say what is wrong and keep the other settings savable.
/// </summary>
public sealed class RapierUnsupportedPlatformTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("71000000-0000-0000-0000-000000000001");
    private static readonly Guid SunsetId = Guid.Parse("71000000-0000-0000-0000-000000000002");

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        DefaultPhysics = "Rapier",
        // Home's own engine is the Rapier it brought; Sunset follows the grid default, which is Rapier too.
        Programs = [new ProgramSettings("Home", 8004, 8011, "Rapier"), new ProgramSettings("Sunset", 8005, 8012)],
        Regions =
        [
            new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
            new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1010, UdpPort: 8005),
        ],
    })
    {
        Before = TestAuth.Apply,
        ExtraServices = s => s.AddSingleton(new PlatformSupport(OSPlatform.OSX, Architecture.Arm64)),
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

    private static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string tab, params (string, string)[] fields)
    {
        var page = Parse(await client.GetStringAsync(Url(id, tab), Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        return await client.PostAsync($"{Url(id, tab)}?handler={tab}", new FormUrlEncodedContent(form), Ct);
    }

    [Theory]
    [InlineData("options", "MaxAgents", "25")]
    [InlineData("scripts", "Scripts", "off")]
    [InlineData("cpu", "Priority", "BelowNormal")]
    public async Task A_saved_Rapier_does_not_block_the_other_tabs(string tab, string field, string value)
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, HomeId, tab, (field, value));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode); // a save redirects back to the tab
        Assert.DoesNotContain("isn't available on this computer", await response.Content.ReadAsStringAsync(Ct).ContinueWith(t => System.Net.WebUtility.HtmlDecode(t.Result), Ct));
        var home = _f.Store.LoadOrCreate().Programs.Single(p => p.Instance == "Home");
        Assert.Equal("Rapier", home.Physics); // untouched
        if (tab == "cpu") Assert.Equal(CpuPriority.BelowNormal, home.Options!.Priority);
    }

    [Fact]
    public async Task The_Physics_tab_says_why_and_does_not_offer_Rapier()
    {
        using var client = await SignedInAsync();

        var own = Parse(await client.GetStringAsync(Url(HomeId, "physics"), Ct));
        var followed = Parse(await client.GetStringAsync(Url(SunsetId, "physics"), Ct));

        Assert.Equal("This program is set to Rapier (experimental), which isn't available on this computer. Pick another engine.",
            own.QuerySelector(".physics-notice")!.TextContent.Trim());
        Assert.Equal("The grid default is set to Rapier (experimental), which isn't available on this computer. Pick another engine.",
            followed.QuerySelector(".physics-notice")!.TextContent.Trim());
        Assert.DoesNotContain("Rapier (experimental)", own.QuerySelectorAll("label.form-check-label").Select(l => l.TextContent));
    }

    [Fact]
    public async Task The_Physics_tab_still_rejects_choosing_Rapier_and_accepts_another_engine()
    {
        using var client = await SignedInAsync();

        using var rejected = await PostAsync(client, HomeId, "physics", ("Physics", "Rapier"));
        Assert.Contains("Rapier isn't available on this computer.", WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync(Ct)));

        using var saved = await PostAsync(client, HomeId, "physics", ("Physics", "BulletSim"));
        Assert.Equal(HttpStatusCode.Found, saved.StatusCode);
        Assert.Equal("BulletSim", _f.Store.LoadOrCreate().Programs.Single(p => p.Instance == "Home").Physics);
    }

    [Fact]
    public async Task Advanced_page_warns_about_a_saved_grid_default_Rapier_and_rejects_setting_it()
    {
        using var client = await SignedInAsync();
        var page = await client.GetStringAsync("/advanced/regions", Ct);
        var doc = Parse(page);

        Assert.Equal("The grid default is set to Rapier (experimental), which isn't available on this computer. Pick another engine and save.",
            doc.QuerySelector(".physics-notice")!.TextContent.Trim());
        Assert.Null(doc.QuerySelector("input[name=DefaultPhysics][value=Rapier]"));

        var token = doc.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var before = File.ReadAllBytes(_f.Paths.SettingsFile);
        using var post = await client.PostAsync("/advanced/regions", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["DefaultPhysics"] = "Rapier", ["DefaultScripts"] = "true",
        }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(_f.Paths.SettingsFile));
    }
}
