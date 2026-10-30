using System.Net;
using DreamGrid.Launcher.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LoginThrottleTests : IDisposable
{
    private static readonly IPAddress Ip5 = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Ip6 = IPAddress.Parse("10.0.0.6");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-throttle-" + Guid.NewGuid().ToString("N"));

    public LoginThrottleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string StateFile => Path.Combine(_dir, "throttle.json");

    [Fact]
    public void Fifth_failure_locks_name_for_ten_minutes()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time, null);
        for (var i = 0; i < 4; i++) throttle.RecordFailure("Test Owner", null);
        Assert.False(throttle.IsLocked("Test Owner", null));
        throttle.RecordFailure("Test Owner", null);
        Assert.True(throttle.IsLocked("test owner", null));
        Assert.False(throttle.IsLocked("Someone Else", null));

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(throttle.IsLocked("Test Owner", null));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(throttle.IsLocked("Test Owner", null));
    }

    [Fact]
    public void Failures_older_than_ten_minutes_do_not_count()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time, null);
        for (var i = 0; i < 4; i++) throttle.RecordFailure("Test Owner", null);
        time.Advance(TimeSpan.FromMinutes(11));
        throttle.RecordFailure("Test Owner", null);
        Assert.False(throttle.IsLocked("Test Owner", null));
    }

    [Fact]
    public void Success_clears_the_failures()
    {
        var throttle = new LoginThrottle(new FakeTimeProvider(), null);
        for (var i = 0; i < 4; i++) throttle.RecordFailure("Test Owner", null);
        throttle.RecordSuccess("Test Owner");
        throttle.RecordFailure("Test Owner", null);
        Assert.False(throttle.IsLocked("Test Owner", null));
    }

    [Fact]
    public void Ip_locks_after_20_failures_across_names()
    {
        var throttle = new LoginThrottle(new FakeTimeProvider(), null);
        for (var i = 0; i < 19; i++) throttle.RecordFailure("Guesser " + i, Ip5);
        Assert.False(throttle.IsLocked("Someone Else", Ip5));
        throttle.RecordFailure("Guesser 19", Ip5);

        Assert.True(throttle.IsLocked("Someone Else", Ip5));
        Assert.False(throttle.IsLocked("Someone Else", Ip6));
        Assert.False(throttle.IsLocked("Someone Else", null));
    }

    [Fact]
    public void Ip_lock_ends_after_ten_minutes()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time, null);
        for (var i = 0; i < LoginThrottle.IpMaxFailures; i++) throttle.RecordFailure("Guesser " + i, Ip5);

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(throttle.IsLocked("Someone Else", Ip5));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(throttle.IsLocked("Someone Else", Ip5));
    }

    [Fact]
    public void A_locked_name_is_locked_from_any_address()
    {
        var throttle = new LoginThrottle(new FakeTimeProvider(), null);
        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++) throttle.RecordFailure("Test Owner", Ip5);

        Assert.True(throttle.IsLocked("Test Owner", Ip6));
        Assert.True(throttle.IsLocked("Test Owner", null));
        Assert.False(throttle.IsLocked("Someone Else", Ip5));
    }

    [Fact]
    public void Success_clears_name_not_ip()
    {
        var throttle = new LoginThrottle(new FakeTimeProvider(), null);
        for (var i = 0; i < 4; i++) throttle.RecordFailure("Test Owner", Ip5);
        for (var i = 0; i < 15; i++) throttle.RecordFailure("Guesser " + i, Ip5);
        throttle.RecordSuccess("Test Owner");

        // The name starts again from zero...
        throttle.RecordFailure("Test Owner", Ip6);
        Assert.False(throttle.IsLocked("Test Owner", Ip6));
        // ...but the 4 + 15 failures from the address stay: the 20th locks it.
        Assert.False(throttle.IsLocked("Someone Else", Ip5));
        throttle.RecordFailure("Another One", Ip5);
        Assert.True(throttle.IsLocked("Someone Else", Ip5));
    }

    [Fact]
    public void Mapped_ipv6_counts_as_ipv4()
    {
        var throttle = new LoginThrottle(new FakeTimeProvider(), null);
        var mapped = IPAddress.Parse("::ffff:10.0.0.5");
        for (var i = 0; i < LoginThrottle.IpMaxFailures; i++) throttle.RecordFailure("Guesser " + i, mapped);

        Assert.True(throttle.IsLocked("Someone Else", Ip5));
        Assert.True(throttle.IsLocked("Someone Else", mapped));
    }

    [Fact]
    public void Lock_survives_new_instance()
    {
        var time = new FakeTimeProvider();
        var first = new LoginThrottle(time, StateFile);
        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++) first.RecordFailure("Test Owner", Ip5);
        Assert.True(first.IsLocked("Test Owner", null));

        var second = new LoginThrottle(time, StateFile);
        Assert.True(second.IsLocked("Test Owner", null));
        Assert.True(second.IsLocked("test owner", Ip6));

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(second.IsLocked("Test Owner", null));
    }

    [Fact]
    public void Ip_lock_and_recent_failures_survive_new_instance()
    {
        var time = new FakeTimeProvider();
        var first = new LoginThrottle(time, StateFile);
        for (var i = 0; i < LoginThrottle.IpMaxFailures; i++) first.RecordFailure("Guesser " + i, Ip5);
        for (var i = 0; i < 4; i++) first.RecordFailure("Test Owner", Ip6);

        var second = new LoginThrottle(time, StateFile);
        Assert.True(second.IsLocked("Someone Else", Ip5));
        // Four earlier failures were kept: one more locks the name.
        Assert.False(second.IsLocked("Test Owner", null));
        second.RecordFailure("Test Owner", null);
        Assert.True(second.IsLocked("Test Owner", null));
    }

    [Fact]
    public void Success_is_kept_across_restarts()
    {
        var time = new FakeTimeProvider();
        var first = new LoginThrottle(time, StateFile);
        for (var i = 0; i < 4; i++) first.RecordFailure("Test Owner", null);
        first.RecordSuccess("Test Owner");

        var second = new LoginThrottle(time, StateFile);
        second.RecordFailure("Test Owner", null);
        Assert.False(second.IsLocked("Test Owner", null));
    }

    [Fact]
    public void The_saved_file_drops_what_has_expired()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time, StateFile);
        throttle.RecordFailure("Old Failure", Ip5);
        time.Advance(TimeSpan.FromMinutes(11));
        throttle.RecordFailure("New Failure", Ip6);

        var json = File.ReadAllText(StateFile);
        Assert.DoesNotContain("old failure", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.0.0.5", json);
        Assert.Contains("New Failure", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(StateFile + ".tmp"));
    }

    [Fact]
    public void Damaged_state_file_starts_empty()
    {
        File.WriteAllText(StateFile, "{not json");
        var logs = new List<(LogLevel Level, string Message)>();

        var time = new FakeTimeProvider();

        var throttle = new LoginThrottle(time, StateFile, new ListLogger(logs));

        Assert.False(throttle.IsLocked("Test Owner", Ip5));
        var warning = Assert.Single(logs, l => l.Level == LogLevel.Warning);
        Assert.DoesNotContain("not json", warning.Message);
        // And it works from there on: the next save replaces the damaged file.
        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++) throttle.RecordFailure("Test Owner", Ip5);
        Assert.True(throttle.IsLocked("Test Owner", Ip5));
        Assert.True(new LoginThrottle(time, StateFile).IsLocked("Test Owner", null));
    }

    [Fact]
    public void Missing_state_file_starts_empty_without_a_warning()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var throttle = new LoginThrottle(new FakeTimeProvider(), StateFile, new ListLogger(logs));

        Assert.False(throttle.IsLocked("Test Owner", Ip5));
        Assert.Empty(logs);
    }

    private sealed class ListLogger(List<(LogLevel, string)> sink) : ILogger<LoginThrottle>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add((logLevel, formatter(state, exception)));
    }
}
