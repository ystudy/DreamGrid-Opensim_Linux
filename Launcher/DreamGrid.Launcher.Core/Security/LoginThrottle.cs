namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// Slows down password guessing: five failures within 10 minutes lock that name for 10 minutes.
/// Names are compared case-insensitively. A success clears the name.
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _names = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public readonly List<DateTimeOffset> Failures = [];
        public DateTimeOffset LockedUntil;
    }

    public bool IsLocked(string name)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            Prune(now);
            return _names.TryGetValue(name, out var e) && e.LockedUntil > now;
        }
    }

    public void RecordFailure(string name)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            Prune(now);
            if (!_names.TryGetValue(name, out var e))
                _names[name] = e = new Entry();
            e.Failures.Add(now);
            if (e.Failures.Count >= MaxFailures)
            {
                e.LockedUntil = now + Window;
                e.Failures.Clear();
            }
        }
    }

    public void RecordSuccess(string name)
    {
        lock (_gate) _names.Remove(name);
    }

    /// <summary>Forgets names whose failures are all old and whose lock is over, so the table cannot grow forever.</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (var (name, e) in _names.ToList())
        {
            e.Failures.RemoveAll(f => now - f >= Window);
            if (e.Failures.Count == 0 && e.LockedUntil <= now) _names.Remove(name);
        }
    }
}
