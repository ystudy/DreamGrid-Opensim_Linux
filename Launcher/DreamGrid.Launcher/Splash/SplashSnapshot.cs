using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Splash;

namespace DreamGrid.Launcher.Splash;

/// <summary>Counts only, never names. A null count means the database couldn't be asked ("Status unavailable").</summary>
public sealed record SplashStatus(bool GridOnline, int? PeopleOnline, int? RegionsRunning);

/// <param name="Version">The published version, or 0 for the built-in page; the stylesheet's address carries it.</param>
public sealed record SplashView(SplashDesign Design, int Version, bool IsDefault, SplashStatus Status, string Css);

/// <summary>What one block of the public page is drawn from.</summary>
public sealed record SplashBlockView(SplashBlock Block, SplashStatus Status, LauncherSettings Settings);

/// <summary>
/// What /welcome shows (spec 2026-11-01-viewer-splash-design §4.7): the published design, or the built-in page when there is
/// none, it fails its check, or this machine has no guest side; plus the live counts. Built at most once a minute by the
/// injected clock, so viewers waiting at a login screen don't each ask the database; concurrent callers share one build.
/// </summary>
public sealed class SplashSnapshot(SplashStore store, IGridController grid, IOnlineUsers online, IGridRegions regions,
    Func<LauncherSettings> settings, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>Where the page's pictures are served (plan 12b).</summary>
    public static string PictureUrl(string name) => "/welcome/images/" + name;

    private readonly SemaphoreSlim _build = new(1, 1);
    private volatile Cached? _cached;
    // The stylesheet of the view before the current one, while pages that name it may still be in viewers' caches.
    private volatile Cached? _previous;
    // The last view built, kept through Invalidate, so a change of version is noticed.
    private Cached? _lastBuilt;

    private sealed record Cached(SplashView View, DateTimeOffset Until);

    public async Task<SplashView> CurrentAsync(CancellationToken ct)
    {
        if (Fresh() is { } view) return view;
        await _build.WaitAsync(ct);
        try
        {
            if (Fresh() is { } built) return built;
            var next = await BuildAsync(ct);
            var now = time.GetUtcNow();
            if (_lastBuilt is { } last && last.View.Version != next.Version) _previous = last with { Until = now + Lifetime };
            _cached = _lastBuilt = new Cached(next, now + Lifetime);
            return next;
        }
        finally
        {
            _build.Release();
        }
    }

    /// <summary>The stylesheet for <paramref name="version"/>: the current view's, or the previous view's for a minute after it changed.</summary>
    public async Task<string?> CssAsync(int version, CancellationToken ct)
    {
        var view = await CurrentAsync(ct);
        if (view.Version == version) return view.Css;
        return _previous is { } p && p.View.Version == version && time.GetUtcNow() < p.Until ? p.View.Css : null;
    }

    /// <summary>Drops the cached view, so a publish shows at once.</summary>
    public void Invalidate() => _cached = null;

    private SplashView? Fresh() => _cached is { } c && time.GetUtcNow() < c.Until ? c.View : null;

    private async Task<SplashView> BuildAsync(CancellationToken ct)
    {
        var people = online.ListAsync(ct);
        var listed = regions.ListAsync(ct);
        var published = AdminRule.HasStaffPages(settings()) ? store.Published() : null;
        var design = published?.Design ?? SplashDefaults.Design();
        var who = await people;
        var where = await listed;
        var status = new SplashStatus(grid.GetStatus().Robust.State == ProcessState.Running,
            who.Available ? who.Avatars.Count : null, where.Available ? where.Regions.Count : null);
        return new SplashView(design, published?.Version ?? 0, published is null, status, SplashCss.Build(design, PictureUrl));
    }
}
