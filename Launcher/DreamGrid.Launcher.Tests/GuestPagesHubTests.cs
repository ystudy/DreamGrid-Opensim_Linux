using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Settings;
using Xunit;
using static DreamGrid.Launcher.Tests.SplashPictureApiTests;

namespace DreamGrid.Launcher.Tests;

/// <summary>The Guest pages hub and the menu's group headings (design 2026-11-05-gridgate-menu-looks-workflows §1).</summary>
public sealed class GuestPagesHubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Owner_sees_every_guest_page()
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "owner"), "/guest-pages");
        Assert.Equal(["/splash", "/security/legal", "/security/wording", "/security/languages", "/security/bots", "/security/iar", "/security/email", "/advanced/password-reset"],
            page.QuerySelectorAll("main .list-group a").Select(a => a.GetAttribute("href")));
    }

    [Fact]
    public async Task Admin_sees_only_the_splash_page()
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "admin"), "/guest-pages");
        Assert.Equal(["/splash"], page.QuerySelectorAll("main .list-group a").Select(a => a.GetAttribute("href")));
    }

    [Fact]
    public async Task Supervisor_cannot_open_it()
    {
        await using var f = Factory();
        using var r = await (await StaffAsync(f, "supervisor")).GetAsync("/guest-pages", Ct);
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task There_is_none_on_a_region_server()
    {
        await using var f = Factory(settings: s => s with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006),
            Hypergrid = false,
        });
        using var r = await (await StaffAsync(f, "owner")).GetAsync("/guest-pages", Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Theory]
    [InlineData("/security/legal")]
    [InlineData("/security/bots")]
    [InlineData("/security/email")]
    [InlineData("/advanced/password-reset")]
    public async Task Moved_pages_link_back_to_guest_pages(string url)
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "owner"), url);
        Assert.Equal("‹ Guest pages", page.QuerySelector("main .back-link a[href='/guest-pages']")!.TextContent.Trim());
    }

    [Fact]
    public async Task Password_reset_left_the_settings_hub()
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "owner"), "/advanced");
        Assert.Null(page.QuerySelector("main a[href='/advanced/password-reset']"));
        Assert.Equal("Settings", page.QuerySelector("h1")!.TextContent.Trim());
    }

    [Fact]
    public async Task Group_headings_link_to_their_hubs_and_sign_out_is_in_the_name_menu()
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "owner"), "/profile/sessions");
        Assert.Equal(["/regions", "/people", "/guest-pages", "/advanced", "/profile/sessions"],
            page.QuerySelectorAll("header nav a.dropdown-toggle").Select(a => a.GetAttribute("href")));
        Assert.NotNull(page.QuerySelectorAll("header nav .dropdown-menu").Last().QuerySelector("form[action='/logout'] button"));
    }

    [Fact]
    public async Task Without_scripts_the_sessions_page_reaches_the_rest_of_your_menu_and_signs_out()
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "owner"), "/profile/sessions");
        Assert.Equal(["/profile/two-factor", "/profile/emails"], page.QuerySelectorAll("#hub-links a").Select(a => a.GetAttribute("href")));
        Assert.Equal("/logout", page.QuerySelector("main form#sign-out-here")!.GetAttribute("action"));
    }

    [Fact]
    public async Task Hubs_link_to_the_rest_of_their_group()
    {
        await using var f = Factory();
        var owner = await StaffAsync(f, "owner");
        Assert.Equal(["/estates", "/hypergrid"],
            (await PageAsync(owner, "/regions")).QuerySelectorAll("#hub-links a").Select(a => a.GetAttribute("href")));
        Assert.Equal(["/people/sign-ups", "/people/tickets", "/groups", "/people/takedowns", "/staff"],
            (await PageAsync(owner, "/people")).QuerySelectorAll("#hub-links a").Select(a => a.GetAttribute("href")));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("supervisor")]
    public async Task Staff_can_open_the_guest_side_and_come_back(string role)
    {
        await using var f = Factory();
        var client = await StaffAsync(f, role);

        var staffPage = await PageAsync(client, "/profile/sessions");
        var link = staffPage.QuerySelector("header a#guest-side-link")!;
        Assert.Equal("/home", link.GetAttribute("href"));
        Assert.Equal("Guest view", link.TextContent.Trim());
        Assert.Null(staffPage.QuerySelector("header nav a#guest-side-link")); // not a menu group

        var front = await PageAsync(client, "/home");
        Assert.NotNull(front.QuerySelector("main form#front-sign-in"));
        Assert.Equal("/", front.QuerySelector("header a#staff-side-link")!.GetAttribute("href"));
        Assert.Null(front.QuerySelector("a#staff-link")); // no "Staff sign in" for someone already signed in
    }

    [Fact]
    public async Task Visitors_get_staff_sign_in_not_the_way_back()
    {
        await using var f = Factory();
        var front = await PageAsync(f.CreateClient(new() { AllowAutoRedirect = false }), "/home");
        Assert.NotNull(front.QuerySelector("header a#staff-link"));
        Assert.Null(front.QuerySelector("#staff-side-link"));
    }

    [Fact]
    public async Task The_hub_links_to_the_guest_side()
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "admin"), "/guest-pages");
        Assert.Equal("/home", page.QuerySelector("main a#guest-side-hub-link")!.GetAttribute("href"));
    }
}
