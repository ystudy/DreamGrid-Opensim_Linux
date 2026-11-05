using DreamGrid.Launcher.Core.Dashboard;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;

namespace DreamGrid.Launcher.Dashboard;

/// <summary>What the dashboard shows of the numbers and checks, and when each was taken.</summary>
public sealed record DashboardView(WorldCounts Counts, DateTimeOffset CheckedAt, IReadOnlyList<ServiceCheck> Checks, DateTimeOffset ChecksAt);

/// <summary>
/// The dashboard's counts, built at most once a minute, and its service checks, at most every 30 seconds (design
/// 2026-11-05-dashboard, "How it updates"), so opening or polling Home never loads the grid. The first caller waits for the
/// first build; after that a caller always gets the last view at once, and a stale one is refreshed in the background, so a
/// slow grid never makes Home slow. Nothing is counted while the database isn't running.
/// </summary>
public sealed class DashboardSnapshot(IWorldCounter counter, IServiceChecks checks, IGridController grid, TimeProvider time,
    ILogger<DashboardSnapshot>? log = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ChecksLifetime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _build = new(1, 1);
    private volatile Stamped<WorldCounts>? _counts;
    private volatile Stamped<IReadOnlyList<ServiceCheck>>? _checks;
    private int _refreshing;

    private sealed record Stamped<T>(T Value, DateTimeOffset At, DateTimeOffset Until);

    public async Task<DashboardView> CurrentAsync(CancellationToken ct)
    {
        if (View() is { } last)
        {
            if (!IsFresh(_counts) || !IsFresh(_checks)) RefreshInBackground();
            return last;
        }
        // The very first build: wait for it (it has its own short limits), but not for the caller's own cancellation.
        await _build.WaitAsync(ct);
        try
        {
            if (View() is { } built) return built;
            await BuildAsync(CancellationToken.None);
            return View()!;
        }
        finally
        {
            _build.Release();
        }
    }

    private void RefreshInBackground()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await _build.WaitAsync();
            try
            {
                await BuildAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                log?.LogWarning("The dashboard's numbers could not be refreshed ({Error}: {Message}).", e.GetType().Name, e.Message);
            }
            finally
            {
                _build.Release();
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private async Task BuildAsync(CancellationToken ct)
    {
        if (!IsFresh(_counts))
        {
            var counts = WorldCounts.Unknown;
            if (grid.GetStatus().Database.State == ProcessState.Running)
            {
                var local = time.GetLocalNow();
                var month = new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, local.Offset);
                var day = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
                counts = await counter.CountAsync(month, day, ct);
            }
            var at = time.GetUtcNow();
            _counts = new Stamped<WorldCounts>(counts, at, at + Lifetime);
        }
        if (!IsFresh(_checks))
        {
            var list = await checks.CheckAsync(_counts!.Value.Profiles is not null, ct);
            var at = time.GetUtcNow();
            _checks = new Stamped<IReadOnlyList<ServiceCheck>>(list, at, at + ChecksLifetime);
        }
    }

    private bool IsFresh<T>(Stamped<T>? s) => s is not null && time.GetUtcNow() < s.Until;

    private DashboardView? View() => _counts is { } c && _checks is { } k ? new DashboardView(c.Value, c.At, k.Value, k.At) : null;
}
