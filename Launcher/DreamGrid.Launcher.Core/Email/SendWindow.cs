namespace DreamGrid.Launcher.Core.Email;

/// <summary>
/// The sending limits, counted over rolling windows (the last 60 minutes and the last 24 hours), since providers don't agree on
/// when their day starts, and the choice of which account sends next.
/// </summary>
public static class SendWindow
{
    public static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    public static readonly TimeSpan Day = TimeSpan.FromHours(24);

    /// <summary>How many of <paramref name="sent"/> fall in the last hour and the last 24 hours.</summary>
    public static (int Hour, int Day) Counts(IEnumerable<DateTimeOffset> sent, DateTimeOffset now)
    {
        int hour = 0, day = 0;
        foreach (var t in sent)
        {
            if (t > now - Day) day++;
            if (t > now - Hour) hour++;
        }
        return (hour, day);
    }

    /// <summary>Whether the account may send one more now.</summary>
    public static bool HasRoom(EmailAccount a, (int Hour, int Day) c) =>
        (a.PerHour is not { } h || c.Hour < h) && (a.PerDay is not { } d || c.Day < d);

    /// <summary>When a full account may send again (the oldest send in the window that is full leaves it), or null if it has room.</summary>
    public static DateTimeOffset? FreesAt(EmailAccount a, IReadOnlyList<DateTimeOffset> sent, DateTimeOffset now)
    {
        DateTimeOffset? at = null;
        if (a.PerHour is { } h) at = Later(at, Frees(sent, now, Hour, h));
        if (a.PerDay is { } d) at = Later(at, Frees(sent, now, Day, d));
        return at;
    }

    /// <summary>How full the account is, 0 to 1: the larger of hour and day used over its limit (0 with no limits).</summary>
    public static double Fullness(EmailAccount a, (int Hour, int Day) c)
    {
        var f = 0.0;
        if (a.PerHour is > 0 and var h) f = Math.Max(f, (double)c.Hour / h);
        if (a.PerDay is > 0 and var d) f = Math.Max(f, (double)c.Day / d);
        return f;
    }

    /// <summary>The account to send with: the lowest order that is not paused, has no problem, isn't resting and has room.</summary>
    public static EmailAccount? Choose(IEnumerable<EmailAccount> accounts, Func<Guid, (int Hour, int Day)> counts, Func<Guid, bool> resting) =>
        accounts.Where(a => !a.Paused && a.Problem is null && !resting(a.Id) && HasRoom(a, counts(a.Id)))
            .OrderBy(a => a.Order)
            .FirstOrDefault();

    private static DateTimeOffset? Frees(IReadOnlyList<DateTimeOffset> sent, DateTimeOffset now, TimeSpan window, int limit)
    {
        var inWindow = sent.Where(t => t > now - window).Order().ToList();
        if (inWindow.Count < limit) return null;
        // The window has room again once enough of the oldest sends have left it.
        return inWindow[inWindow.Count - limit] + window;
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
