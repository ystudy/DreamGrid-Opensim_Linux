using System.Net;
using System.Text.Json;
using DreamGrid.Launcher.Core.Dashboard;
using DreamGrid.Launcher.Dashboard;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DreamGrid.Launcher.Tests.SplashPictureApiTests;

namespace DreamGrid.Launcher.Tests;

/// <summary>The dashboard's counts are cached a minute, and /api/dashboard is for admins (design 2026-11-05-dashboard).</summary>
public sealed class DashboardSnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorldCounts Some(long residents) => WorldCounts.Unknown with { Residents = residents };

    [Fact]
    public async Task Counts_are_reused_for_a_minute_then_counted_again()
    {
        var counter = new FakeWorldCounter { Result = Some(5) };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        var snapshot = new DashboardSnapshot(counter, new FakeServiceChecks(), new FakeGridController(), time);

        Assert.Equal(5, (await snapshot.CurrentAsync(Ct)).Counts.Residents);
        counter.Result = Some(6);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(5, (await snapshot.CurrentAsync(Ct)).Counts.Residents);
        Assert.Equal(1, counter.Calls);
        time.Advance(TimeSpan.FromSeconds(2));
        // A stale view comes back at once, and is refreshed in the background.
        Assert.Equal(5, (await snapshot.CurrentAsync(Ct)).Counts.Residents);
        for (var i = 0; i < 100 && counter.Calls < 2; i++) await Task.Delay(20, Ct);
        for (var i = 0; i < 100 && (await snapshot.CurrentAsync(Ct)).Counts.Residents != 6; i++) await Task.Delay(20, Ct);
        Assert.Equal(6, (await snapshot.CurrentAsync(Ct)).Counts.Residents);
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task Callers_at_the_same_moment_share_one_count()
    {
        var counter = new FakeWorldCounter { Result = Some(1), Delay = TimeSpan.FromMilliseconds(200) };
        var snapshot = new DashboardSnapshot(counter, new FakeServiceChecks(), new FakeGridController(), new FakeTimeProvider());

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => snapshot.CurrentAsync(Ct)));

        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task Nothing_is_counted_while_the_database_is_down()
    {
        var counter = new FakeWorldCounter { Result = Some(1) };
        var grid = new FakeGridController();
        grid.Status = grid.GetStatus() with { Database = grid.GetStatus().Database with { State = DreamGrid.Launcher.Core.Processes.ProcessState.Stopped } };
        var view = await new DashboardSnapshot(counter, new FakeServiceChecks(), grid, new FakeTimeProvider()).CurrentAsync(Ct);

        Assert.Null(view.Counts.Residents);
        Assert.Equal(0, counter.Calls);
    }

    [Fact]
    public async Task Api_gives_admins_the_counts_without_caching()
    {
        await using var f = Factory();
        f.Counts.Result = Some(128);
        using var r = await (await StaffAsync(f, "admin")).GetAsync("/api/dashboard", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(128, json.GetProperty("counts").GetProperty("residents").GetInt64());
    }

    [Theory]
    [InlineData("supervisor")]
    [InlineData(null)]
    public async Task Api_refuses_others(string? role)
    {
        await using var f = Factory();
        var client = role is null ? Client(f) : await StaffAsync(f, role);
        using var r = await client.GetAsync("/api/dashboard", Ct);
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }
}
