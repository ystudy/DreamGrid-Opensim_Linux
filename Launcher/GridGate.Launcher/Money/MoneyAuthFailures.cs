using System.Collections.Concurrent;
using System.Net;

namespace GridGate.Launcher.Money;

/// <summary>
/// Wrong region keys per address (money design §7.2): after <see cref="Limit"/> failures within <see cref="Window"/>, the
/// address is refused (429) for <see cref="Window"/>. One warning line per address per window, naming the address and the
/// count, never what was sent.
/// </summary>
public sealed class MoneyAuthFailures(TimeProvider time, ILogger<MoneyAuthFailures> log)
{
    public const int Limit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private sealed class Tally
    {
        public DateTimeOffset Since;
        public int Count;
        public DateTimeOffset BlockedUntil;
        public bool Warned;
    }

    private readonly ConcurrentDictionary<IPAddress, Tally> _tallies = new();

    public bool IsBlocked(IPAddress ip) =>
        _tallies.TryGetValue(ip, out var t) && Locked(t, () => t.BlockedUntil > time.GetUtcNow());

    public void RecordFailure(IPAddress ip)
    {
        var t = _tallies.GetOrAdd(ip, _ => new Tally { Since = time.GetUtcNow() });
        var (count, warn) = Locked(t, () =>
        {
            var now = time.GetUtcNow();
            if (now - t.Since > Window && t.BlockedUntil <= now)
                (t.Since, t.Count, t.Warned) = (now, 0, false);
            t.Count++;
            if (t.Count >= Limit && t.BlockedUntil <= now)
                t.BlockedUntil = now + Window;
            var first = t.Count >= Limit && !t.Warned;
            if (first) t.Warned = true;
            return (t.Count, first);
        });
        if (warn)
            log.LogWarning("Refusing money requests from {Address} for {Minutes} minutes: {Count} had a wrong region key.", ip, (int)Window.TotalMinutes, count);
    }

    private static T Locked<T>(Tally t, Func<T> f)
    {
        lock (t) return f();
    }
}
