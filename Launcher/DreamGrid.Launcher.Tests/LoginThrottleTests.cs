using DreamGrid.Launcher.Core.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LoginThrottleTests
{
    [Fact]
    public void Fifth_failure_locks_name_for_ten_minutes()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < 4; i++) throttle.RecordFailure("Test Owner");
        Assert.False(throttle.IsLocked("Test Owner"));
        throttle.RecordFailure("Test Owner");
        Assert.True(throttle.IsLocked("test owner"));
        Assert.False(throttle.IsLocked("Someone Else"));

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(throttle.IsLocked("Test Owner"));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(throttle.IsLocked("Test Owner"));
    }

    [Fact]
    public void Failures_older_than_ten_minutes_do_not_count()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < 4; i++) throttle.RecordFailure("Test Owner");
        time.Advance(TimeSpan.FromMinutes(11));
        throttle.RecordFailure("Test Owner");
        Assert.False(throttle.IsLocked("Test Owner"));
    }

    [Fact]
    public void Success_clears_the_failures()
    {
        var throttle = new LoginThrottle(new FakeTimeProvider());
        for (var i = 0; i < 4; i++) throttle.RecordFailure("Test Owner");
        throttle.RecordSuccess("Test Owner");
        throttle.RecordFailure("Test Owner");
        Assert.False(throttle.IsLocked("Test Owner"));
    }
}
