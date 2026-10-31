using System.Collections.Concurrent;
using System.Text.Json;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Moving a region into its owner's estate and back, against a fake grid (the console dialogues are the controller's tests).</summary>
public sealed class RegionEstateSyncTests : IDisposable
{
    private static readonly Guid Beach = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid Isle = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-00000000000a");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-estates-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _logs = new();
    private readonly FakeGridController _grid = new();
    private readonly RegionRights _rights;
    private readonly AuditLog _audit;

    public RegionEstateSyncTests()
    {
        _rights = new RegionRights(Path.Combine(_dir, "guests", "region-rights.json"), _time);
        _audit = new AuditLog(AuditFile, _time);
        // Beach and Isle, each in a program of its own; the grid's estate is 1, and new estates get 100, 101, …
        var settings = new SettingsStore(Path.Combine(_dir, "settings.json")).LoadOrCreate() with
        {
            GridName = "My World",
            Programs = [new ProgramSettings("Beach", 8004, 8011), new ProgramSettings("Isle", 8005, 8012)],
            Regions = [new RegionSettings("Beach", "Beach", Beach, UdpPort: 8004), new RegionSettings("Isle", "Isle", Isle, X: 1010, UdpPort: 8005)],
        };
        _grid.SettingsSource = () => settings;
        var next = 100;
        _grid.OnCreateEstate = (_, _, name) => (next++, name);
    }

    public void Dispose()
    {
        _audit.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string AuditFile => Path.Combine(_dir, "audit.log");

    private RegionEstateSync New() =>
        new(_grid, _rights, _audit, _time, new LoggerFactory([new CapturingLoggerProvider(_logs)]).CreateLogger<RegionEstateSync>());

    /// <summary>The estate calls the fake grid got, in order.</summary>
    private string[] EstateCalls() =>
        _grid.Snapshot().Where(c => c.StartsWith("EstateOf ", StringComparison.Ordinal) || c.StartsWith("CreateEstate ", StringComparison.Ordinal)
            || c.StartsWith("LinkEstate ", StringComparison.Ordinal)).ToArray();

    private int Warnings => _logs.Count(l => l.Level == LogLevel.Warning);

    [Fact]
    public async Task Pending_move_applied_when_region_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var sync = New();
        _rights.Grant(Beach, Ann, "Ann Guest");
        _grid.ProgramStates["Beach"] = ProcessState.Stopped;

        await sync.SyncOnceAsync(ct);

        Assert.Empty(EstateCalls());
        Assert.Equal(EstateMove.ToOwner, _rights.Of(Beach)!.Pending);

        _grid.ProgramStates["Beach"] = ProcessState.Running;
        await sync.SyncOnceAsync(ct);

        Assert.Equal([$"EstateOf {Beach}", $"CreateEstate {Beach} {Ann} Ann Guest Estate", $"LinkEstate {Beach} 100"], EstateCalls());
        Assert.Null(_rights.Of(Beach)!.Pending);
        Assert.Equal(1, _rights.HomeEstateOf(Beach));
        Assert.Equal(100, _rights.EstateOf(Ann));
        Assert.Equal(0, Warnings);

        await sync.SyncOnceAsync(ct); // nothing left to do
        Assert.Equal(3, EstateCalls().Length);
    }

    [Fact]
    public async Task Second_region_reuses_owners_estate()
    {
        var ct = TestContext.Current.CancellationToken;
        var sync = New();
        _rights.Grant(Beach, Ann, "Ann Guest");
        _rights.Grant(Isle, Ann, "Ann Guest");

        await sync.SyncOnceAsync(ct);

        var calls = EstateCalls();
        Assert.Single(calls, c => c.StartsWith("CreateEstate ", StringComparison.Ordinal));
        Assert.Contains($"LinkEstate {Beach} 100", calls);
        Assert.Contains($"LinkEstate {Isle} 100", calls);
        Assert.Empty(_rights.PendingMoves());
    }

    [Fact]
    public async Task Remove_links_back_to_home_estate()
    {
        var ct = TestContext.Current.CancellationToken;
        var sync = New();
        _rights.Grant(Beach, Ann, "Ann Guest");
        await sync.SyncOnceAsync(ct);

        _rights.Remove(Beach);
        await sync.SyncOnceAsync(ct);

        Assert.Equal($"LinkEstate {Beach} 1", EstateCalls()[^1]);
        Assert.Null(_rights.Of(Beach));
        Assert.Null(_rights.HomeEstateOf(Beach));
        Assert.Equal(100, _rights.EstateOf(Ann)); // the owner's estate is never deleted
        Assert.Equal(0, Warnings);
    }

    [Fact]
    public async Task Removed_right_without_a_remembered_home_stays_and_warns()
    {
        var ct = TestContext.Current.CancellationToken;
        _rights.Grant(Beach, Ann, "Ann Guest");
        _rights.MoveDone(Beach); // moved, but where it came from was lost
        _rights.Remove(Beach);

        await New().SyncOnceAsync(ct);

        Assert.Empty(EstateCalls());
        Assert.Null(_rights.Of(Beach));
        Assert.Equal(1, Warnings);
    }

    [Fact]
    public async Task Remove_during_a_move_still_moves_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var sync = New();
        _rights.Grant(Beach, Ann, "Ann Guest");
        _grid.OnLinkEstate = (region, _) => { if (_rights.Of(region)?.Pending == EstateMove.ToOwner) _rights.Remove(region); };

        await sync.SyncOnceAsync(ct);

        // The region went into Ann's estate while its right was taken away: it still has to go back.
        Assert.Equal(EstateMove.ToGrid, _rights.Of(Beach)!.Pending);
        Assert.Equal(1, _rights.HomeEstateOf(Beach));

        await sync.SyncOnceAsync(ct);

        Assert.Equal($"LinkEstate {Beach} 1", EstateCalls()[^1]);
        Assert.Null(_rights.Of(Beach));
    }

    [Fact]
    public async Task Remove_before_the_first_move_began_stops_it()
    {
        var ct = TestContext.Current.CancellationToken;
        _rights.Grant(Beach, Ann, "Ann Guest");
        // Taken away while the sync reads where the region is now: it never moved, so the right just goes.
        _grid.OnEstateOf = region => { _rights.Remove(region); return 1; };

        await New().SyncOnceAsync(ct);

        Assert.Equal([$"EstateOf {Beach}"], EstateCalls());
        Assert.Null(_rights.Of(Beach));
        Assert.Null(_rights.HomeEstateOf(Beach));
        Assert.Null(_rights.EstateOf(Ann));
        Assert.False(File.Exists(AuditFile) && File.ReadAllText(AuditFile).Length > 0, "a move was audited");
    }

    [Fact]
    public async Task A_right_removed_while_another_region_moves_is_not_moved()
    {
        var ct = TestContext.Current.CancellationToken;
        _rights.Grant(Beach, Ann, "Ann Guest");
        _rights.Grant(Isle, Ann, "Ann Guest");
        _grid.OnLinkEstate = (region, _) => { if (region == Beach) _rights.Remove(Isle); };

        await New().SyncOnceAsync(ct);

        Assert.DoesNotContain(EstateCalls(), c => c.Contains(Isle.ToString(), StringComparison.Ordinal));
        Assert.Null(_rights.Of(Isle));
        Assert.Null(_rights.HomeEstateOf(Isle));
    }

    [Fact]
    public async Task Regrant_during_a_move_back_keeps_the_new_move()
    {
        var ct = TestContext.Current.CancellationToken;
        var bob = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
        var sync = New();
        _rights.Grant(Beach, Ann, "Ann Guest");
        await sync.SyncOnceAsync(ct);
        _rights.Remove(Beach);
        _grid.OnLinkEstate = (region, estate) => { if (estate == 1) _rights.Grant(region, bob, "Bob Guest"); };

        await sync.SyncOnceAsync(ct);

        // Back in the grid's estate, and handed to Bob meanwhile: his move is still to do, from the same home.
        var r = _rights.Of(Beach)!;
        Assert.Equal((bob, EstateMove.ToOwner), (r.Owner, r.Pending));
        Assert.Equal(1, _rights.HomeEstateOf(Beach));

        _grid.OnLinkEstate = (_, _) => { };
        await sync.SyncOnceAsync(ct);

        Assert.Equal([$"CreateEstate {Beach} {bob} Bob Guest Estate", $"LinkEstate {Beach} 101"], EstateCalls()[^2..]);
        Assert.Single(EstateCalls(), c => c.StartsWith("EstateOf ", StringComparison.Ordinal)); // the home was kept, not read again
        Assert.Null(_rights.Of(Beach)!.Pending);
        Assert.Equal(1, _rights.HomeEstateOf(Beach));
    }

    [Fact]
    public async Task Failure_keeps_move_pending_and_warns_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var sync = New();
        _rights.Grant(Beach, Ann, "Ann Guest");
        var fail = true;
        _grid.OnLinkEstate = (_, _) => { if (fail) throw new InvalidOperationException("The console said no."); };

        await sync.SyncOnceAsync(ct);
        await sync.SyncOnceAsync(ct);

        Assert.Equal(EstateMove.ToOwner, _rights.Of(Beach)!.Pending);
        var warning = Assert.Single(_logs, l => l.Level == LogLevel.Warning);
        Assert.Contains("Beach", warning.Message);
        Assert.Contains("The console said no.", warning.Message);
        // The estate made the first time is used again, not made twice.
        Assert.Single(EstateCalls(), c => c.StartsWith("CreateEstate ", StringComparison.Ordinal));

        fail = false;
        await sync.SyncOnceAsync(ct);
        Assert.Null(_rights.Of(Beach)!.Pending);

        // A later failure is told again.
        _rights.Remove(Beach);
        fail = true;
        await sync.SyncOnceAsync(ct);
        Assert.Equal(2, Warnings);
        Assert.Equal(EstateMove.ToGrid, _rights.Of(Beach)!.Pending);
    }

    [Fact]
    public async Task A_lost_owner_estate_is_made_again()
    {
        var ct = TestContext.Current.CancellationToken;
        _rights.SetEstate(Ann, 55, "Ann Guest Estate");
        _rights.Grant(Beach, Ann, "Ann Guest");
        _grid.OnLinkEstate = (_, estate) =>
        {
            if (estate == 55) throw new InvalidOperationException("The region did not join estate 55. Console: No estate found with ID 55");
        };

        await New().SyncOnceAsync(ct);

        Assert.Equal([$"EstateOf {Beach}", $"LinkEstate {Beach} 55", $"CreateEstate {Beach} {Ann} Ann Guest Estate", $"LinkEstate {Beach} 100"], EstateCalls());
        Assert.Equal(100, _rights.EstateOf(Ann));
        Assert.Null(_rights.Of(Beach)!.Pending);
    }

    [Fact]
    public async Task Moves_are_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var sync = New();
        _grid.OnCreateEstate = (_, _, name) => (100, name + " 2"); // the name was taken
        _rights.Grant(Beach, Ann, "Ann Guest");
        await sync.SyncOnceAsync(ct);
        _rights.Remove(Beach);
        await sync.SyncOnceAsync(ct);

        _audit.Dispose(); // closes audit.log
        var lines = File.ReadAllLines(AuditFile).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(["Beach → Ann Guest Estate 2", "Beach → My World Estate"], lines.Select(l => l.GetProperty("target").GetString()));
        Assert.All(lines, l =>
        {
            Assert.Equal("DreamGrid", l.GetProperty("actor").GetString());
            Assert.Equal(JsonValueKind.Null, l.GetProperty("actorId").ValueKind);
            Assert.Equal("system", l.GetProperty("role").GetString());
            Assert.Equal(JsonValueKind.Null, l.GetProperty("ip").ValueKind);
            Assert.Equal("estate-move", l.GetProperty("action").GetString());
            Assert.Equal("ok", l.GetProperty("result").GetString());
        });
        Assert.Equal("Ann Guest Estate 2", _rights.OwnerEstateOf(Ann)!.Name);
    }
}
