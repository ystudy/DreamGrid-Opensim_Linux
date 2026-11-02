using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Splash;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The splash design's draft and published copies under Data/splash (spec 2026-11-01-viewer-splash-design §4.1, §5).</summary>
public sealed class SplashStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-splash-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly SplashStore _store;

    public SplashStoreTests()
    {
        Paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        _store = new SplashStore(Paths, _time);
    }

    private LauncherPaths Paths { get; }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static SplashDesign Design(string text) => new(1280, 800, new SplashBackground("#1a2a3a", null, 50, 50, 0), false, 16,
    [
        new SplashBlock(1, SplashBlockKind.Text, 40, 40, 400, 80, 1, "#000000", 50, null, false, 8,
            new SplashFont(24, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans),
            new SplashContent(Text: new Dictionary<string, string> { ["en"] = text })),
    ]);

    private static string TextOf(SplashDocument? d) => d!.Design.Blocks[0].Content.Text!["en"];

    [Fact]
    public void Save_then_load_round_trips()
    {
        var r = Assert.IsType<SplashSaveResult.Saved>(_store.SaveDraft(Design("Hello"), 0, "Test Owner"));
        Assert.Equal(1, r.Draft.Version);

        var draft = new SplashStore(Paths, _time).Draft();
        Assert.NotNull(draft);
        Assert.Equal(1, draft.Version);
        Assert.Equal("Test Owner", draft.SavedBy);
        Assert.Equal(_time.GetUtcNow(), draft.Saved);
        Assert.Equal("Hello", TextOf(draft));
        Assert.Equal(SplashBlockKind.Text, draft.Design.Blocks[0].Kind);
    }

    [Fact]
    public void Stale_draft_save_is_a_conflict()
    {
        _store.SaveDraft(Design("one"), 0, "A");
        _store.SaveDraft(Design("two"), 1, "B");

        var r = Assert.IsType<SplashSaveResult.Conflict>(_store.SaveDraft(Design("three"), 1, "C"));

        Assert.Equal(2, r.CurrentVersion);
        Assert.Equal(2, _store.Draft()!.Version);
        Assert.Equal("two", TextOf(_store.Draft()));
    }

    [Fact]
    public void Invalid_design_is_not_saved()
    {
        _store.SaveDraft(Design("good"), 0, "A");
        var bad = Design("bad") with { Width = 10 };

        var r = Assert.IsType<SplashSaveResult.Invalid>(_store.SaveDraft(bad, 1, "A"));

        Assert.NotEmpty(r.Problems);
        Assert.Equal("good", TextOf(_store.Draft()));
    }

    [Fact]
    public void Publish_copies_draft_and_counts_versions()
    {
        Assert.Null(_store.Publish("A"));

        _store.SaveDraft(Design("first"), 0, "A");
        Assert.Equal(1, _store.Publish("A")!.Version);
        _store.SaveDraft(Design("second"), 1, "B");
        var second = _store.Publish("B")!;

        Assert.Equal(2, second.Version);
        Assert.Equal("B", second.SavedBy);
        Assert.Equal("second", TextOf(new SplashStore(Paths, _time).Published()));
    }

    [Fact]
    public void Discard_keeps_published()
    {
        _store.SaveDraft(Design("live"), 0, "A");
        _store.Publish("A");
        _store.SaveDraft(Design("draft"), 1, "A");

        _store.Discard();

        Assert.Null(_store.Draft());
        Assert.Equal("live", TextOf(_store.Published()));
        // The published counter carries on after a discard, and so does the draft's.
        Assert.IsType<SplashSaveResult.Saved>(_store.SaveDraft(Design("again"), _store.DraftVersion(), "A"));
        Assert.Equal(2, _store.Publish("A")!.Version);
    }

    [Fact]
    public void Discard_does_not_reuse_draft_versions()
    {
        // A loaded draft v1; B discarded it and started again; A's save based on v1 must not land on B's new draft.
        _store.SaveDraft(Design("A's start"), 0, "A");
        _store.Discard();
        Assert.Equal(1, _store.DraftVersion());
        Assert.IsType<SplashSaveResult.Conflict>(_store.SaveDraft(Design("B's"), 0, "B"));
        var b = Assert.IsType<SplashSaveResult.Saved>(_store.SaveDraft(Design("B's"), 1, "B"));
        Assert.Equal(2, b.Draft.Version);

        Assert.IsType<SplashSaveResult.Conflict>(_store.SaveDraft(Design("A's late save"), 1, "A"));
        Assert.Equal("B's", TextOf(_store.Draft()));
    }

    [Fact]
    public void Invalid_published_file_is_ignored()
    {
        _store.SaveDraft(Design("live"), 0, "A");
        _store.Publish("A");
        var file = Path.Combine(Paths.Splash, "published.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"x\": 40", "\"x\": -5"));

        Assert.Null(new SplashStore(Paths, _time).Published());
    }

    [Fact]
    public void Files_are_utf8_without_bom()
    {
        _store.SaveDraft(Design("Grüße"), 0, "A");

        var bytes = File.ReadAllBytes(Path.Combine(Paths.Splash, "draft.json"));

        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
        Assert.Contains("Grüße", TextOf(_store.Draft()));
    }
}
