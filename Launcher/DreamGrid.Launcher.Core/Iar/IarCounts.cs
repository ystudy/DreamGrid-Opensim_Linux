using System.Security.Cryptography;
using System.Text;
using DreamGrid.Launcher.Core.Grid;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Iar;

/// <summary>
/// How many backups and uploads each resident started over the last 24 hours (spec 2026-11-11-iar-backups-design §8). Counted per
/// account, so other addresses don't help. Kept in <c>file</c> (<c>&lt;security&gt;/iar-counts.json</c>) with each account as a
/// SHA-256 key, never in plain text. A missing or damaged file starts the counts again. Safe to call from any thread.
/// </summary>
public sealed class IarCounts
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly JsonStateFile<Dictionary<string, List<DateTimeOffset>>> _file;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _counts;

    public IarCounts(string file, TimeProvider time, ILogger<IarCounts>? log = null)
    {
        _time = time;
        _file = new JsonStateFile<Dictionary<string, List<DateTimeOffset>>>(file, () => [], log);
        _counts = _file.Load();
    }

    public int UsedToday(Guid principal, IarJobKind kind)
    {
        lock (_gate) return Recent(Key(principal, kind)).Count;
    }

    /// <summary>Counts one job started now.</summary>
    public void Use(Guid principal, IarJobKind kind)
    {
        lock (_gate)
        {
            Recent(Key(principal, kind)).Add(_time.GetUtcNow());
            _file.Save(_counts);
        }
    }

    /// <summary>Gives the newest count back (the job failed because of the grid, not the resident). Nothing to give back is fine.</summary>
    public void GiveBack(Guid principal, IarJobKind kind)
    {
        lock (_gate)
        {
            var list = Recent(Key(principal, kind));
            if (list.Count == 0) return;
            list.RemoveAt(list.Count - 1);
            _file.Save(_counts);
        }
    }

    // Under the lock: the person's times within the window, oldest first; older ones are dropped.
    private List<DateTimeOffset> Recent(string key)
    {
        var since = _time.GetUtcNow() - Window;
        if (!_counts.TryGetValue(key, out var list)) _counts[key] = list = [];
        list.RemoveAll(t => t <= since);
        list.Sort();
        return list;
    }

    private static string Key(Guid principal, IarJobKind kind) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{principal:D}")));
}
