namespace DreamGrid.Launcher.Email;

/// <summary>At most 5 test emails a minute for each signed-in person, so the test button can't be used to flood a mailbox.</summary>
public sealed class EmailTestLimiter(TimeProvider time)
{
    public const int PerMinute = 5;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _sent = [];

    /// <summary>Takes one send for <paramref name="who"/>; false when they have used up this minute's.</summary>
    public bool TryTake(string who)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_sent.TryGetValue(who, out var list)) _sent[who] = list = [];
            list.RemoveAll(t => t <= now - TimeSpan.FromMinutes(1));
            if (list.Count >= PerMinute) return false;
            list.Add(now);
            return true;
        }
    }
}
