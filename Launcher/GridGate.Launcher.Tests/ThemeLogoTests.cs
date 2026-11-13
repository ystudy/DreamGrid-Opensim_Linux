using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Splash;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static GridGate.Launcher.Tests.SplashPictureApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>The grid's one logo (spec §4.2): set by the owner or an admin, shown in the splash page's grid-name block.</summary>
public sealed class ThemeLogoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void PublishNameBlock(LauncherFactory f)
    {
        var store = f.Services.GetRequiredService<SplashStore>();
        var design = new SplashDesign(1280, 800, new SplashBackground("#223344", null, 50, 50, 0), false, 16,
        [
            new SplashBlock(1, SplashBlockKind.GridName, 40, 40, 600, 120, 1, "#000000", 100, null, false, 0,
                new SplashFont(48, "#ffffff", true, SplashAlign.Left, SplashFontFamily.Sans), new SplashContent(ShowName: true, ShowLogo: true)),
        ]);
        Assert.IsType<SplashSaveResult.Saved>(store.SaveDraft(design, store.DraftVersion(), "Test Owner"));
        store.Publish("Test Owner");
    }

    private static async Task<AngleSharp.Html.Dom.IHtmlDocument> WelcomeAsync(LauncherFactory f) =>
        new HtmlParser().ParseDocument(await Client(f).GetStringAsync("/welcome", Ct));

    [Fact]
    public async Task Owner_sets_logo_and_page_shows_it()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        PublishNameBlock(f);

        var logo = await UploadAsync(client, await TokenAsync(client), TestPictures.Png(64, 64), "logo.png", "/api/theme/logo");

        var img = (await WelcomeAsync(f)).QuerySelector("img.splash-logo");
        Assert.NotNull(img);
        Assert.Equal("/theme/images/" + logo.Name, img.GetAttribute("src"));
        Assert.Equal("Avalon", img.GetAttribute("alt"));
        using var r = await Client(f).GetAsync("/theme/images/" + logo.Name, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("immutable", r.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Replacing_the_logo_removes_the_old_file()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "admin");
        var token = await TokenAsync(client);

        var first = await UploadAsync(client, token, TestPictures.Png(64, 64), "one.png", "/api/theme/logo");
        var second = await UploadAsync(client, token, TestPictures.Png(32, 32), "two.png", "/api/theme/logo");

        using var old = await Client(f).GetAsync("/theme/images/" + first.Name, Ct);
        using var current = await Client(f).GetAsync("/theme/images/" + second.Name, Ct);
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.False(File.Exists(Path.Combine(f.Paths.ThemeImages, first.Name)));
    }

    [Fact]
    public async Task Removing_the_logo()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var token = await TokenAsync(client);
        PublishNameBlock(f);
        var logo = await UploadAsync(client, token, TestPictures.Png(64, 64), "logo.png", "/api/theme/logo");

        using var removed = await SendAsync(client, HttpMethod.Delete, "/api/theme/logo", token);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var page = await WelcomeAsync(f);
        Assert.Null(page.QuerySelector("img.splash-logo"));
        Assert.Contains("Avalon", page.Body!.TextContent);
        using var gone = await Client(f).GetAsync("/theme/images/" + logo.Name, Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Only_the_current_logo_is_public()
    {
        await using var f = Factory();

        using var r = await Client(f).GetAsync("/theme/images/" + new string('a', 32) + ".png", Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Supervisor_cannot_set_logo()
    {
        await using var f = Factory();
        var supervisor = await StaffAsync(f, "supervisor");

        using var r = await SendAsync(supervisor, HttpMethod.Post, "/api/theme/logo", null, new ByteArrayContent(TestPictures.Png(8, 8)));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Logo_changes_are_audited()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var token = await TokenAsync(client);

        var logo = await UploadAsync(client, token, TestPictures.Png(64, 64), "logo.png", "/api/theme/logo");
        (await SendAsync(client, HttpMethod.Delete, "/api/theme/logo", token)).Dispose();

        var audit = AuditText(f);
        Assert.Contains("theme-logo-set", audit);
        Assert.Contains(logo.Name, audit);
        Assert.Contains("theme-logo-removed", audit);
    }

    [Fact]
    public async Task Theme_images_reachable_with_admin_network_list()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            // Every caller comes from outside the network list set below.
            Before = app =>
            {
                app.Use((ctx, next) =>
                {
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
                    return next(ctx);
                });
                TestAuth.Apply(app);
            },
        };
        await using var _ = f;
        TestAuth.AddStaff(f.Robust);
        var client = await StaffAsync(f, "owner");
        var logo = await UploadAsync(client, await TokenAsync(client), TestPictures.Png(64, 64), "logo.png", "/api/theme/logo");

        f.Store.Save(f.Store.LoadOrCreate() with { Security = f.Store.LoadOrCreate().Security with { AdminNetworks = ["10.0.0.0/8"] } });

        using var picture = await Client(f).GetAsync("/theme/images/" + logo.Name, Ct);
        using var staff = await Client(f).GetAsync("/login", Ct);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, staff.StatusCode);
    }
}
