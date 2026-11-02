using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Splash;
using Xunit;
using static DreamGrid.Launcher.Tests.SplashPictureApiTests;

namespace DreamGrid.Launcher.Tests;

/// <summary>Staff see the draft drawn by the same code as the public page (plan 12c Task 1); nobody else does.</summary>
public sealed class SplashDraftViewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static SplashDesign Design(string text, SplashBackground? background = null) =>
        new(1280, 800, background ?? new SplashBackground("#223344", null, 50, 50, 0), false, 16,
        [
            new SplashBlock(1, SplashBlockKind.Text, 40, 40, 600, 80, 1, "#000000", 50, null, false, 8,
                new SplashFont(24, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans),
                new SplashContent(Text: new Dictionary<string, string> { ["en"] = text })),
        ]);

    private static async Task<int> SaveAsync(HttpClient client, string token, SplashDesign design)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/splash", Ct));
        var basedOn = doc.RootElement.GetProperty("draftVersion").GetInt32();
        using var r = await SendAsync(client, HttpMethod.Put, "/api/splash/draft?basedOn=" + basedOn, token, JsonContent.Create(design, options: Json));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<SplashDocument>(Json, Ct))!.Version;
    }

    [Fact]
    public async Task Working_view_is_the_draft_when_there_is_one()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var version = await SaveAsync(client, await TokenAsync(client), Design("Draft words"));

        var html = await client.GetStringAsync("/splash/preview", Ct);

        Assert.Contains("Draft words", html);
        var page = new HtmlParser().ParseDocument(html);
        Assert.NotNull(page.QuerySelector($"link[href=\"/api/splash/draft.css?v={version}\"]"));
    }

    [Fact]
    public async Task Working_view_falls_back_to_published_then_default()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        Assert.Contains("Avalon", await client.GetStringAsync("/splash/preview", Ct));

        var token = await TokenAsync(client);
        await SaveAsync(client, token, Design("Published words"));
        using (var p = await SendAsync(client, HttpMethod.Post, "/api/splash/publish", token)) Assert.Equal(HttpStatusCode.OK, p.StatusCode);

        Assert.Contains("Published words", await client.GetStringAsync("/splash/preview", Ct));
    }

    [Fact]
    public async Task Draft_css_uses_staff_picture_urls()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var token = await TokenAsync(client);
        var picture = await UploadAsync(client, token, TestPictures.Png(40, 30));
        await SaveAsync(client, token, Design("Hi", new SplashBackground("#223344", new SplashPicture(picture.Name, SplashFit.Fill), 50, 50, 0)));

        using var r = await client.GetAsync("/api/splash/draft.css?v=1", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/css", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", r.Headers.CacheControl?.ToString());
        var css = await r.Content.ReadAsStringAsync(Ct);
        Assert.Contains($"url(\"/api/splash/pictures/{picture.Name}\")", css);
        Assert.StartsWith("body.splash, .splash-canvas{", css);
    }

    [Theory]
    [InlineData("/api/splash/draft.css")]
    [InlineData("/splash/preview")]
    public async Task Draft_routes_are_staff_only(string url)
    {
        await using var f = Factory();
        Assert.NotEqual(HttpStatusCode.OK, (await Client(f).GetAsync(url, Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await (await StaffAsync(f, "supervisor")).GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await StaffAsync(f, "owner")).GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await StaffAsync(f, "admin")).GetAsync(url, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("/api/splash/draft.css")]
    [InlineData("/splash/preview")]
    public async Task Draft_routes_are_404_on_a_region_server(string url)
    {
        await using var f = Factory(settings: s => s with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006),
            Hypergrid = false,
        });
        var client = await StaffAsync(f, "owner");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url, Ct)).StatusCode);
    }
}
