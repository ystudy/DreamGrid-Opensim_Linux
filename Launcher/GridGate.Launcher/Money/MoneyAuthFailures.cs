using System.Collections.Concurrent;
using System.Net;

namespace GridGate.Launcher.Money;

/// <summary>
/// Wrong region keys per address (money design §7.2: "slowed down and logged"). Past <see cref="Limit"/> failures within
/// <see cref="Window"/>, each further wrong key waits <see cref="SlowDown"/> before its 401. Nothing is ever blocked: every
/// region program calls from 127.0.0.1, so blocking an address would let one stale key stop money for all of them (review
/// I2). One warning line per address per window, naming the address and the count, never what was sent.
/// </summary>
public sealed class MoneyAuthFailures(TimeProvider time, ILogger<MoneyAuthFailures> log)
{
    public const int Limit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SlowDown = TimeSpan.FromSeconds(1);

    private sealed class Tally
    {
        public DateTimeOffset Since;
        public int Count;
        public bool Warned;
    }

    private readonly ConcurrentDictionary<IPAddress, Tally> _tallies = new();

    /// <summary>Counts a wrong key from <paramref name="ip"/>; how long to wait before answering it.</summary>
    public TimeSpan RecordFailure(IPAddress ip)
    {
        var t = _tallies.GetOrAdd(ip, _ => new Tally { Since = time.GetUtcNow() });
        int count;
        bool warn;
        lock (t)
        {
            var now = time.GetUtcNow();
            if (now - t.Since > Window)
                (t.Since, t.Count, t.Warned) = (now, 0, false);
            count = ++t.Count;
            warn = count > Limit && !t.Warned;
            if (warn) t.Warned = true;
        }
        if (warn)
            log.LogWarning("Slowing down money requests with a wrong region key from {Address}: {Count} in the last {Minutes} minutes.",
                ip, count, (int)Window.TotalMinutes);
        return count > Limit ? SlowDown : TimeSpan.Zero;
    }
}
