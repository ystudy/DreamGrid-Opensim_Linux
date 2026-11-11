using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The Landing section of the Regions page (2.14): a default region and fallback regions.</summary>
public sealed class LandingPageTests : IAsyncLifetime
{
    private static readonly Guid Welcome = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Beach = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Forest = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private readonly LauncherFactory _f = Factory(null);

    private static LauncherFactory Factory(OwnerPlan? plan) => new(s => s with
    {
        SetupToken = null,
        Plan = plan,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        Programs = TestSettings.ProgramFor(new RegionSettings("Welcome", "Welcome", Welcome)),
        Regions =
        [
            new RegionSettings("Welcome", "Welcome", Welcome, UdpPort: 8004),
            new RegionSettings("Welcome", "Beach", Beach, 1001, 1000, UdpPort: 8005),
            new RegionSettings("Welcome", "Forest", Forest, 1002, 1000, UdpPort: 8006),
        ],
    })
    {
        Before = TestAuth.Apply,
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    private static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, params (string Key, string Value)[] fields)
    {
        var token = Parse(await client.GetStringAsync("/regions", Ct))
            .QuerySelector("#landing input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
            .Append(new("__RequestVerificationToken", token));
        return await client.PostAsync("/regions?handler=landing", new FormUrlEncodedContent(form), Ct);
    }

    [Fact]
    public async Task Section_offers_every_region_and_names_the_automatic_default()
    {
        using var client = await SignedInAsync(_f);
        var doc = Parse(await client.GetStringAsync("/regions", Ct));

        var options = doc.QuerySelectorAll("#landing select[name=LandingDefault] option").ToList();
        Assert.Equal(["", Welcome.ToString(), Beach.ToString(), Forest.ToString()], options.Select(o => o.GetAttribute("value")));
        Assert.Equal("Automatic: Welcome", options[0].TextContent.Trim());
        Assert.True(options[0].HasAttribute("selected"));
        Assert.Equal([Welcome.ToString(), Beach.ToString(), Forest.ToString()],
            doc.QuerySelectorAll("#landing input[name=LandingFallbacks]").Select(i => i.GetAttribute("value")));
        Assert.Empty(doc.QuerySelectorAll("#landing input[name=LandingFallbacks][checked]"));
        var text = doc.QuerySelector("#landing")!.TextContent;
        Assert.Contains("nearest", text);
        Assert.Contains("restart", text);
    }

    [Fact]
    public async Task Saving_keeps_the_choice_writes_the_config_and_says_a_restart_applies_it()
    {
        using var client = await SignedInAsync(_f);

        using var resp = await PostAsync(client, ("LandingDefault", Forest.ToString()),
            ("LandingFallbacks", Welcome.ToString()), ("LandingFallbacks", Beach.ToString()), ("LandingFallbacks", Forest.ToString()));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var saved = _f.Store.LoadOrCreate().Landing!;
        Assert.Equal(Forest, saved.Default);
        Assert.Equal([Welcome, Beach], saved.Fallbacks!); // the default is not its own fallback
        Assert.True(_f.Grid.UpdateWriteConfig[^1]);

        var doc = Parse(await client.GetStringAsync("/regions", Ct));
        Assert.Contains("restarts", doc.QuerySelector(".notice")!.TextContent);
        Assert.True(doc.QuerySelector($"#landing option[value='{Forest}']")!.HasAttribute("selected"));
        Assert.Equal([Welcome.ToString(), Beach.ToString()],
            doc.QuerySelectorAll("#landing input[name=LandingFallbacks][checked]").Select(i => i.GetAttribute("value")));
    }

    [Fact]
    public async Task Choosing_automatic_clears_the_default()
    {
        using var client = await SignedInAsync(_f);
        (await PostAsync(client, ("LandingDefault", Beach.ToString()))).Dispose();

        using var resp = await PostAsync(client, ("LandingDefault", ""));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Null(_f.Store.LoadOrCreate().Landing!.Default);
    }

    [Theory]
    [InlineData("LandingDefault")]
    [InlineData("LandingFallbacks")]
    public async Task A_region_that_is_not_one_of_yours_is_refused_and_nothing_saved(string field)
    {
        using var client = await SignedInAsync(_f);

        using var resp = await PostAsync(client, (field, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(_f.Store.LoadOrCreate().Landing);
    }

    [Fact]
    public async Task A_region_server_has_no_landing_section()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var client = await SignedInAsync(f);

        var doc = Parse(await client.GetStringAsync("/regions", Ct));

        Assert.Null(doc.QuerySelector("#landing"));
    }
}
