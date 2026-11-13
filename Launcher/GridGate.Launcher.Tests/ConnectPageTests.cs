using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Pages.Guest;
using Xunit;
using static GridGate.Launcher.Tests.SplashPictureApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>The guest Connect page: viewers, grid manager settings, steps and help (2026-11-05 connect page mockup).</summary>
public sealed class ConnectPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IHtmlDocument> PageAsync(LauncherFactory f)
    {
        using var r = await Client(f).GetAsync("/guest/connect", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Settings_block_uses_the_grids_own_addresses()
    {
        await using var f = Factory(settings: s => s with { Hostname = "grid.example.org", WebPort = 8001 });
        var page = await PageAsync(f);

        var text = page.QuerySelector("#grid-settings")!.TextContent;
        Assert.Contains("Grid name:  Avalon", text);
        Assert.Contains("Login page: http://grid.example.org:8001/welcome", text);
        Assert.Contains("Account:    http://grid.example.org:8001/signup", text);
        Assert.Contains("Password:   http://grid.example.org:8001/guest/forgot-password", text);
        Assert.StartsWith("Login URI:  " + page.QuerySelector("#login-uri")!.TextContent, text);
        Assert.NotNull(page.QuerySelector("button#copy-settings[hidden]")); // shown by connect.js
    }

    [Fact]
    public async Task Settings_match_what_robust_is_told()
    {
        await using var f = Factory(settings: s => s with { Hostname = "grid.example.org" });
        var s = f.Store.LoadOrCreate();
        var text = (await PageAsync(f)).QuerySelector("#grid-settings")!.TextContent;
        Assert.Contains(WebsiteAddress.Welcome(s), text);
        Assert.Contains(WebsiteAddress.SignUp(s), text);
        Assert.Contains(WebsiteAddress.ForgotPassword(s), text);
    }

    [Fact]
    public async Task Viewers_link_to_their_own_sites_and_nothing_is_hosted_here()
    {
        await using var f = Factory();
        var page = await PageAsync(f);

        Assert.Equal(ConnectModel.Firestorm.Url, page.QuerySelector("#viewer-firestorm a")!.GetAttribute("href"));
        Assert.Equal(ConnectModel.Others.Select(v => v.Url), page.QuerySelectorAll("#viewer-others a").Select(a => a.GetAttribute("href")));
        Assert.All(page.QuerySelectorAll("#viewer-firestorm a, #viewer-others a"), a =>
        {
            Assert.StartsWith("https://", a.GetAttribute("href"));
            Assert.Equal("noopener noreferrer", a.GetAttribute("rel"));
        });
    }

    [Fact]
    public async Task Help_lists_only_the_legal_pages_that_are_written()
    {
        await using var f = Factory();
        var none = await PageAsync(f);
        Assert.Null(none.QuerySelector("#help a[href='/terms']"));
        Assert.Null(none.QuerySelector("#help a[href='/contact']"));
        Assert.NotNull(none.QuerySelector("#help a[href='/signup']"));
        Assert.NotNull(none.QuerySelector("#help a[href='/guest/forgot-password']"));
    }

    [Fact]
    public async Task Guest_layout_loads_the_copy_script()
    {
        await using var f = Factory();
        var page = await PageAsync(f);
        var scripts = page.QuerySelectorAll("script[src]").Select(s => s.GetAttribute("src")).ToList();
        Assert.True(scripts.IndexOf("/js/copy.js") >= 0 && scripts.IndexOf("/js/copy.js") < scripts.IndexOf("/js/connect.js"));
    }
}
