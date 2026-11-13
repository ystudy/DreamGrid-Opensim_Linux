using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Splash;
using GridGate.Launcher.Splash;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Which pictures a splash design uses, and the clean-up of the ones nothing has used for 30 days (spec §4.4).</summary>
public sealed class SplashPicturesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SplashFont Font = new(24, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans);

    private static string Name(char c) => new string(c, 32) + ".png";

    private static SplashBlock Block(int id, SplashBlockKind kind, SplashContent content, SplashPicture? background = null) =>
        new(id, kind, 0, id * 20, 100, 20, id, "#000000", 0, background, false, 0, Font, content);

    private static SplashDesign Design(params SplashBlock[] blocks) =>
        new(1280, 800, new SplashBackground("#000000", null, 50, 50, 0), false, 16, blocks);

    private static SplashDesign PictureDesign(string name) =>
        Design(Block(1, SplashBlockKind.Picture, new(Picture: new SplashPicture(name, SplashFit.Fill))));

    [Fact]
    public void UsedBy_finds_every_kind_of_reference()
    {
        var d = Design(
                Block(1, SplashBlockKind.Picture, new(Picture: new SplashPicture(Name('a'), SplashFit.Fit))),
                Block(2, SplashBlockKind.Text, new(Text: new Dictionary<string, string> { ["en"] = "Hi" }), new SplashPicture(Name('b'), SplashFit.Tile)),
                Block(3, SplashBlockKind.Places, new(Places:
                [
                    new SplashPlace(Guid.NewGuid(), "Sandbox", new Dictionary<string, string> { ["en"] = "" }, Name('c')),
                    new SplashPlace(Guid.NewGuid(), "Beach", new Dictionary<string, string> { ["en"] = "" }, null),
                ])))
            with { Background = new SplashBackground("#000000", new SplashPicture(Name('d'), SplashFit.Fill), 50, 50, 0) };

        Assert.Equal(new[] { Name('a'), Name('b'), Name('c'), Name('d') }, SplashPictures.UsedBy(d).Order());
        Assert.Empty(SplashPictures.UsedBy(null));
        Assert.Empty(SplashPictures.UsedBy(Design(null!)));
    }

    [Fact]
    public async Task Designs_with_stored_pictures_can_be_saved()
    {
        await using var f = new LauncherFactory();
        var pictures = f.Services.GetRequiredKeyedService<PictureStore>("splash");
        var store = f.Services.GetRequiredService<SplashStore>();
        var added = Assert.IsType<PictureResult.Added>(await pictures.AddAsync(new MemoryStream(TestPictures.Png(8, 8)), "a.png", "Test Owner", Ct));

        var first = store.SaveDraft(PictureDesign(added.Picture.Name), 0, "Test Owner");
        Assert.True(first is SplashSaveResult.Saved, string.Join(" | ", (first as SplashSaveResult.Invalid)?.Problems ?? []));
        Assert.IsType<SplashSaveResult.Invalid>(store.SaveDraft(PictureDesign(Name('e')), 1, "Test Owner"));
    }

    [Fact]
    public async Task InUse_counts_draft_and_published()
    {
        await using var f = new LauncherFactory();
        var (pictures, store) = (f.Services.GetRequiredKeyedService<PictureStore>("splash"), f.Services.GetRequiredService<SplashStore>());
        var live = await AddAsync(pictures);
        var draft = await AddAsync(pictures);
        var neither = await AddAsync(pictures);
        store.SaveDraft(PictureDesign(live), 0, "A");
        store.Publish("A");
        store.SaveDraft(PictureDesign(draft), 1, "A");

        var inUse = SplashPictures.InUse(store);

        Assert.Contains(live, inUse);
        Assert.Contains(draft, inUse);
        Assert.DoesNotContain(neither, inUse);
    }

    private static async Task<string> AddAsync(PictureStore pictures) =>
        Assert.IsType<PictureResult.Added>(await pictures.AddAsync(new MemoryStream(TestPictures.Png(8, 8)), "p.png", "Test Owner", Ct)).Picture.Name;

    private static string AuditText(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public async Task Cleanup_removes_pictures_unused_for_30_days()
    {
        await using var f = new LauncherFactory();
        var pictures = f.Services.GetRequiredKeyedService<PictureStore>("splash");
        var cleanup = f.Services.GetRequiredService<SplashPictureCleanup>();
        var name = await AddAsync(pictures);

        await cleanup.RunOnceAsync(Ct);
        f.Time.Advance(TimeSpan.FromDays(29));
        await cleanup.RunOnceAsync(Ct);
        Assert.True(pictures.Exists(name));

        f.Time.Advance(TimeSpan.FromDays(1));
        await cleanup.RunOnceAsync(Ct);

        Assert.False(pictures.Exists(name));
        Assert.Contains("splash-image-deleted", AuditText(f));
        Assert.Contains("unused for 30 days", AuditText(f));
    }

    [Fact]
    public async Task Cleanup_spares_a_picture_used_again()
    {
        await using var f = new LauncherFactory();
        var (pictures, store) = (f.Services.GetRequiredKeyedService<PictureStore>("splash"), f.Services.GetRequiredService<SplashStore>());
        var cleanup = f.Services.GetRequiredService<SplashPictureCleanup>();
        var name = await AddAsync(pictures);

        await cleanup.RunOnceAsync(Ct);
        f.Time.Advance(TimeSpan.FromDays(20));
        store.SaveDraft(PictureDesign(name), 0, "A");
        await cleanup.RunOnceAsync(Ct);
        store.Discard();
        f.Time.Advance(TimeSpan.FromDays(20));
        await cleanup.RunOnceAsync(Ct);

        // Unused again only for the last 20 days.
        Assert.True(pictures.Exists(name));
    }

    [Fact]
    public async Task Cleanup_keeps_published_pictures()
    {
        await using var f = new LauncherFactory();
        var (pictures, store) = (f.Services.GetRequiredKeyedService<PictureStore>("splash"), f.Services.GetRequiredService<SplashStore>());
        var cleanup = f.Services.GetRequiredService<SplashPictureCleanup>();
        var name = await AddAsync(pictures);
        store.SaveDraft(PictureDesign(name), 0, "A");
        store.Publish("A");
        store.Discard();

        for (var day = 0; day < 60; day += 10)
        {
            await cleanup.RunOnceAsync(Ct);
            f.Time.Advance(TimeSpan.FromDays(10));
        }

        Assert.True(pictures.Exists(name));
    }

    [Fact]
    public async Task Publish_refuses_a_draft_whose_picture_is_gone()
    {
        await using var f = new LauncherFactory();
        var (pictures, store) = (f.Services.GetRequiredKeyedService<PictureStore>("splash"), f.Services.GetRequiredService<SplashStore>());
        var name = await AddAsync(pictures);
        store.SaveDraft(PictureDesign(name), 0, "A");
        // Gone between the save and the publish (another admin, or the clean-up).
        pictures.Delete(name);

        Assert.Null(store.Publish("A"));
        Assert.Null(store.Published());
    }

    [Fact]
    public async Task RemovePictureIfUnused_keeps_a_picture_the_page_uses()
    {
        await using var f = new LauncherFactory();
        var (pictures, store) = (f.Services.GetRequiredKeyedService<PictureStore>("splash"), f.Services.GetRequiredService<SplashStore>());
        var name = await AddAsync(pictures);
        store.SaveDraft(PictureDesign(name), 0, "A");

        Assert.False(store.RemovePictureIfUnused(name, pictures.Delete));
        Assert.True(pictures.Exists(name));

        store.Discard();
        Assert.True(store.RemovePictureIfUnused(name, pictures.Delete));
        Assert.False(pictures.Exists(name));
    }

    [Fact]
    public void Splash_pictures_live_under_the_splash_folder()
    {
        var paths = new LauncherPaths("data", "program", null);
        Assert.Equal(Path.Combine("data", "splash", "images"), paths.SplashImages);
        Assert.Equal(Path.Combine("data", "theme", "images"), paths.ThemeImages);
    }
}
