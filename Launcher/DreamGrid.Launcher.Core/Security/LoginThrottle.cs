using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// Slows down password guessing, by name and by address. Five failures for one name within 10 minutes lock that name for
/// 10 minutes; 20 failures from one address within 10 minutes lock that address for 10 minutes (whatever names were tried).
/// Names are compared case-insensitively; an IPv4-mapped IPv6 address counts as its IPv4 address. A sign-in is refused when
/// either its name or its address is locked. A success clears the name only: the address keeps its failures, so one good
/// account cannot be used to wash a guesser's count. A caller whose address is unknown (null) is counted by name only.
/// The state is written to <c>stateFile</c> after each change and read back at construction, so a restart does not give a
/// guesser a fresh start. A missing or damaged file means an empty start (damaged: one warning); a failed save is logged
/// and the throttle carries on in memory.
/// </summary>
public sealed class LoginThrottle
{
    public const int NameMaxFailures = 5;
    public const int IpMaxFailures = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly TimeProvider _time;
    private readonly string? _stateFile;
    private readonly ILogger<LoginThrottle>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _ips = new(StringComparer.Ordinal);

    public LoginThrottle(TimeProvider time, string? stateFile, ILogger<LoginThrottle>? log = null)
    {
        _time = time;
        _stateFile = stateFile;
        _log = log;
        Load();
    }

    private sealed class Entry
    {
        public readonly List<DateTimeOffset> Failures = [];
        public DateTimeOffset LockedUntil;
    }

    /// <summary>The saved form: only what is needed to rebuild the locks and the recent failures.</summary>
    private sealed record State(Dictionary<string, SavedEntry>? Names, Dictionary<string, SavedEntry>? Ips);

    private sealed record SavedEntry(List<DateTimeOffset>? Failures, DateTimeOffset? LockedUntil);

    public bool IsLocked(string name, IPAddress? ip)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            return LockedAt(_names, name, now) || (ip is not null && LockedAt(_ips, Normalise(ip), now));
        }
    }

    public void RecordFailure(string name, IPAddress? ip)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            Add(_names, name, NameMaxFailures, now);
            if (ip is not null) Add(_ips, Normalise(ip), IpMaxFailures, now);
            Save();
        }
    }

    /// <summary>Clears the name. The address's failures stay.</summary>
    public void RecordSuccess(string name)
    {
        lock (_gate)
        {
            if (!_names.Remove(name)) return;
            Save();
        }
    }

    private static bool LockedAt(Dictionary<string, Entry> table, string key, DateTimeOffset now) =>
        table.TryGetValue(key, out var e) && e.LockedUntil > now;

    private static void Add(Dictionary<string, Entry> table, string key, int max, DateTimeOffset now)
    {
        if (!table.TryGetValue(key, out var e))
            table[key] = e = new Entry();
        e.Failures.Add(now);
        if (e.Failures.Count >= max)
        {
            e.LockedUntil = now + Window;
            e.Failures.Clear();
        }
    }

    /// <summary>IPv4-mapped IPv6 (::ffff:a.b.c.d) is its IPv4 address; an IPv6 zone is dropped.</summary>
    private static string Normalise(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.ScopeId != 0) ip = new IPAddress(ip.GetAddressBytes());
        return ip.ToString();
    }

    /// <summary>Forgets entries whose failures are all old and whose lock is over, so the tables cannot grow forever.</summary>
    private static void Prune(Dictionary<string, Entry> table, DateTimeOffset now)
    {
        foreach (var (key, e) in table.ToList())
        {
            e.Failures.RemoveAll(f => now - f >= Window);
            if (e.Failures.Count == 0 && e.LockedUntil <= now) table.Remove(key);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        Prune(_names, now);
        Prune(_ips, now);
    }

    private void Load()
    {
        if (_stateFile is null || !File.Exists(_stateFile)) return;
        try
        {
            string json;
            using (var fs = new FileStream(_stateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();
            var state = JsonSerializer.Deserialize<State>(json, Options) ?? throw new JsonException("the file contains null");
            Restore(state.Names, _names);
            Restore(state.Ips, _ips);
            Prune(_time.GetUtcNow());
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // The file is only a memory of recent failures: start empty rather than refuse to start. Only the exception
            // type is logged, as its message could quote the file.
            _names.Clear();
            _ips.Clear();
            _log?.LogWarning("The sign-in throttle file {File} could not be read ({Error}); starting with no recorded failures.",
                _stateFile, e.GetType().Name);
        }
    }

    private static void Restore(Dictionary<string, SavedEntry>? saved, Dictionary<string, Entry> into)
    {
        if (saved is null) return;
        foreach (var (key, s) in saved)
        {
            var e = new Entry { LockedUntil = s?.LockedUntil ?? default };
            if (s?.Failures is { } failures) e.Failures.AddRange(failures);
            into[key] = e;
        }
    }

    private void Save()
    {
        if (_stateFile is null) return;
        try
        {
            var state = new State(Snapshot(_names), Snapshot(_ips));
            AtomicFile.WriteAllText(_stateFile, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning("The sign-in throttle file {File} could not be saved ({Error}); failures are kept in memory only.",
                _stateFile, e.GetType().Name);
        }
    }

    private static Dictionary<string, SavedEntry> Snapshot(Dictionary<string, Entry> table) =>
        table.ToDictionary(kv => kv.Key, kv => new SavedEntry([.. kv.Value.Failures], kv.Value.LockedUntil == default ? null : kv.Value.LockedUntil),
            table.Comparer);
}
