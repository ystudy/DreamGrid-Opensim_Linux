using DreamGrid.Launcher.Core.Email;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Rolling hourly and daily limits, and which account sends next.</summary>
public sealed class SendWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static EmailAccount Account(int order, int? perDay = 500, int? perHour = null, bool paused = false, string? problem = null) =>
        new(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "a@example.com", "x", "Grid", "a@example.com", null,
            perDay, perHour, paused, order, problem, problem is null ? null : Now);

    [Fact]
    public void Counts_use_rolling_windows()
    {
        var sent = new[] { Now.AddMinutes(-59), Now.AddMinutes(-61), Now.AddHours(-23), Now.AddHours(-25) };
        Assert.Equal((1, 3), SendWindow.Counts(sent, Now));
    }

    [Fact]
    public void HasRoom_respects_both_limits()
    {
        var a = Account(1, perDay: 10, perHour: 2);
        Assert.True(SendWindow.HasRoom(a, (1, 9)));
        Assert.False(SendWindow.HasRoom(a, (2, 2)));
        Assert.False(SendWindow.HasRoom(a, (0, 10)));
        Assert.True(SendWindow.HasRoom(Account(1, perDay: null), (1000, 100000)));
    }

    [Fact]
    public void Choose_skips_paused_problem_resting_and_full()
    {
        var paused = Account(1, paused: true);
        var broken = Account(2, problem: "The server refused the password");
        var resting = Account(3);
        var full = Account(4, perDay: 5);
        var good = Account(5);
        var counts = new Dictionary<Guid, (int, int)> { [full.Id] = (0, 5) };
        var chosen = SendWindow.Choose([good, full, resting, broken, paused], id => counts.GetValueOrDefault(id), id => id == resting.Id);
        Assert.Equal(good.Id, chosen?.Id);
        Assert.Null(SendWindow.Choose([paused, broken], _ => (0, 0), _ => false));
    }

    [Fact]
    public void Choose_prefers_lowest_order()
    {
        var second = Account(2);
        var first = Account(1);
        Assert.Equal(first.Id, SendWindow.Choose([second, first], _ => (0, 0), _ => false)?.Id);
    }

    [Fact]
    public void FreesAt_is_oldest_send_in_window_plus_window()
    {
        var hourly = Account(1, perDay: null, perHour: 2);
        var sent = new List<DateTimeOffset> { Now.AddMinutes(-50), Now.AddMinutes(-10) };
        Assert.Equal(Now.AddMinutes(10), SendWindow.FreesAt(hourly, sent, Now));
        var daily = Account(1, perDay: 2);
        Assert.Equal(Now.AddHours(-23).AddHours(24), SendWindow.FreesAt(daily, [Now.AddHours(-23), Now.AddHours(-1)], Now));
        Assert.Null(SendWindow.FreesAt(daily, [Now.AddHours(-1)], Now));
    }

    [Fact]
    public void Fullness_uses_the_tighter_limit()
    {
        Assert.Equal(0.8, SendWindow.Fullness(Account(1, perDay: 400, perHour: 50), (40, 100)), 3);
        Assert.Equal(0, SendWindow.Fullness(Account(1, perDay: null), (40, 100)));
    }
}
