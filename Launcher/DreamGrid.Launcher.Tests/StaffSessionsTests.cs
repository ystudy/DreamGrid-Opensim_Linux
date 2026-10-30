using System.Collections.Concurrent;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class StaffSessionsTests : IDisposable
{
    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    public StaffSessionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string StateFile => Path.Combine(_dir, "sessions.json");

    private StaffSessions New() => new(StateFile, _time);

    [Fact]
    public void Start_then_valid()
    {
        var sessions = New();
        var s = sessions.Start(Ann, "10.0.0.5", "Firefox");
        var stamp = sessions.StampOf(Ann);

        Assert.True(sessions.IsValid(Ann, s.Id, stamp));
        Assert.False(sessions.IsValid(Ann, s.Id, stamp + "x"));
        Assert.False(sessions.IsValid(Ann, "not-a-session", stamp));
        Assert.False(sessions.IsValid(Bob, s.Id, stamp));
        Assert.Matches("^[0-9a-f]{32}$", s.Id);
        Assert.Matches("^[0-9a-f]{32}$", stamp);
        var listed = Assert.Single(sessions.List(Ann));
        Assert.Equal(new StaffSession(s.Id, _time.GetUtcNow(), _time.GetUtcNow(), "10.0.0.5", "Firefox"), listed);

        // Kept across a restart.
        var again = New();
        Assert.Equal(stamp, again.StampOf(Ann));
        Assert.True(again.IsValid(Ann, s.Id, stamp));
    }

    [Fact]
    public void EndAll_invalidates_all_and_rotates_stamp()
    {
        var sessions = New();
        var a = sessions.Start(Ann, null, null);
        var b = sessions.Start(Ann, null, null);
        var other = sessions.Start(Bob, null, null);
        var stamp = sessions.StampOf(Ann);

        sessions.EndAll(Ann);

        Assert.False(sessions.IsValid(Ann, a.Id, stamp));
        Assert.False(sessions.IsValid(Ann, b.Id, stamp));
        Assert.NotEqual(stamp, sessions.StampOf(Ann));
        Assert.Empty(sessions.List(Ann));
        Assert.True(sessions.IsValid(Bob, other.Id, sessions.StampOf(Bob)));
        Assert.Empty(New().List(Ann));
    }

    [Fact]
    public void End_one_keeps_others()
    {
        var sessions = New();
        var a = sessions.Start(Ann, null, null);
        var b = sessions.Start(Ann, null, null);
        var stamp = sessions.StampOf(Ann);

        sessions.End(Ann, a.Id);

        Assert.False(sessions.IsValid(Ann, a.Id, stamp));
        Assert.True(sessions.IsValid(Ann, b.Id, stamp));
        Assert.Equal(stamp, sessions.StampOf(Ann));
        Assert.Equal([b.Id], New().List(Ann).Select(s => s.Id));
    }

    [Fact]
    public void Damaged_file_reads_as_empty()
    {
        File.WriteAllText(StateFile, "{\"people\": [not json");
        var logs = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(logs)));

        var sessions = new StaffSessions(StateFile, _time, factory.CreateLogger<StaffSessions>());

        var warning = Assert.Single(logs, l => l.Level == LogLevel.Warning);
        Assert.Contains("could not be read", warning.Message);
        Assert.DoesNotContain("not json", warning.Message); // the file's content is never quoted
        Assert.Empty(sessions.List(Ann));
        var s = sessions.Start(Ann, null, null);
        Assert.True(sessions.IsValid(Ann, s.Id, sessions.StampOf(Ann)));
    }

    [Fact]
    public void Touch_writes_at_most_once_a_minute()
    {
        var sessions = New();
        var s = sessions.Start(Ann, null, null);
        var text = File.ReadAllText(StateFile);
        var written = File.GetLastWriteTimeUtc(StateFile);

        _time.Advance(TimeSpan.FromSeconds(10));
        sessions.Touch(Ann, s.Id);
        _time.Advance(TimeSpan.FromSeconds(10));
        sessions.Touch(Ann, s.Id);

        Assert.Equal(text, File.ReadAllText(StateFile));
        Assert.Equal(written, File.GetLastWriteTimeUtc(StateFile));

        _time.Advance(TimeSpan.FromMinutes(1));
        sessions.Touch(Ann, s.Id);
        Assert.NotEqual(text, File.ReadAllText(StateFile));
        Assert.Equal(_time.GetUtcNow(), Assert.Single(New().List(Ann)).LastSeen);
    }

    [Fact]
    public void Reading_never_writes()
    {
        var sessions = New();
        var s = sessions.Start(Ann, null, null);
        var stamp = sessions.StampOf(Ann);
        var text = File.ReadAllText(StateFile);
        _time.Advance(TimeSpan.FromHours(9));

        Assert.False(sessions.IsValid(Ann, s.Id, stamp)); // past the cookie's lifetime
        Assert.Empty(sessions.List(Ann));
        Assert.Equal(text, File.ReadAllText(StateFile));
    }

    [Fact]
    public void Sessions_idle_past_the_cookie_lifetime_are_dropped_on_write()
    {
        var sessions = New();
        var old = sessions.Start(Ann, null, null);
        _time.Advance(TimeSpan.FromHours(9));

        var fresh = sessions.Start(Ann, null, null);

        Assert.Equal([fresh.Id], sessions.List(Ann).Select(x => x.Id));
        Assert.DoesNotContain(old.Id, File.ReadAllText(StateFile));
    }
}
