using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>Which sign-in door a name is counted at. Each door keeps its own count for each name; the address count is shared.</summary>
public enum SignInDoor
{
    /// <summary>The staff side: <c>/login</c>, <c>/login/code</c>, the two-factor page and the port-8001 command API.</summary>
    Staff,

    /// <summary>The guest door, <c>/guest/sign-in</c>.</summary>
    Guest,
}

/// <summary>
/// Slows down password guessing, by name and by address. Five failures for one name at one door within 10 minutes lock that
/// name at that door for 10 minutes; 20 failures from one address within 10 minutes lock that address for 10 minutes at every
/// door (whatever names were tried, through whichever door). Each <see cref="SignInDoor"/> has its own name table, so failures
/// at the guest door can't lock a name out of the staff door, and a success at the guest door (password alone) can't clear
/// the staff count of wrong two-factor codes. The tables are separate in memory and in the file, so no typed name can reach
/// the other door's entry. Names are compared case-insensitively and are never stored as typed (a password pasted into the
/// name field must not reach the disk, and a huge name must not become a huge key): each name table, in memory and in the
/// file, is keyed by the SHA-256 hex of the lowercased name. An IPv4-mapped IPv6 address counts as its IPv4 address. A
/// sign-in is refused when either its name (at its door) or its address is locked. A success clears the name at that door
/// only: the address keeps its failures, so one good account cannot be used to wash a guesser's count. A caller whose
/// address is unknown (null) is counted by name only. The state is written to <c>stateFile</c> after each change and read
/// back at construction, so a restart does not give a guesser a fresh start; a file from before the guest door (one name
/// table) loads as the staff door's. A missing or damaged file means an empty start (damaged: one warning); a failed save
/// is logged and the throttle carries on in memory.
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
    private bool _saveFailing;
    private readonly Dictionary<string, Entry> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _guestNames = new(StringComparer.Ordinal);
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

    /// <summary>The saved form: only what is needed to rebuild the locks and the recent failures. <c>names</c> is the staff
    /// door's table (as it was before the guest door existed), <c>guestNames</c> the guest door's.</summary>
    private sealed record State(Dictionary<string, SavedEntry>? Names, Dictionary<string, SavedEntry>? Ips,
        Dictionary<string, SavedEntry>? GuestNames);

    private sealed record SavedEntry(List<DateTimeOffset>? Failures, DateTimeOffset? LockedUntil);

    public bool IsLocked(SignInDoor door, string name, IPAddress? ip)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (Prune(now)) Save();
            return LockedAt(NamesAt(door), NameKey(name), now) || (ip is not null && LockedAt(_ips, Normalise(ip), now));
        }
    }

    public void RecordFailure(SignInDoor door, string name, IPAddress? ip)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            Add(NamesAt(door), NameKey(name), NameMaxFailures, now);
            if (ip is not null) Add(_ips, Normalise(ip), IpMaxFailures, now);
            Save();
        }
    }

    /// <summary>Clears the name at <paramref name="door"/> only. The other door's count and the address's failures stay.</summary>
    public void RecordSuccess(SignInDoor door, string name)
    {
        lock (_gate)
        {
            if (!NamesAt(door).Remove(NameKey(name))) return;
            Save();
        }
    }

    private Dictionary<string, Entry> NamesAt(SignInDoor door) => door switch
    {
        SignInDoor.Staff => _names,
        SignInDoor.Guest => _guestNames,
        _ => throw new ArgumentOutOfRangeException(nameof(door)),
    };

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

    /// <summary>The name table's key: SHA-256 hex of the lowercased name, so nothing typed is stored and every key is 64 characters.</summary>
    private static string NameKey(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((name ?? "").ToLowerInvariant())));

    /// <summary>IPv4-mapped IPv6 (::ffff:a.b.c.d) is its IPv4 address; an IPv6 zone is dropped.</summary>
    private static string Normalise(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.ScopeId != 0) ip = new IPAddress(ip.GetAddressBytes());
        return ip.ToString();
    }

    /// <summary>Forgets entries whose failures are all old and whose lock is over, so the tables cannot grow forever.</summary>
    private static bool Prune(Dictionary<string, Entry> table, DateTimeOffset now)
    {
        var changed = false;
        foreach (var (key, e) in table.ToList())
        {
            changed |= e.Failures.RemoveAll(f => now - f >= Window) > 0;
            if (e.Failures.Count == 0 && e.LockedUntil <= now) { table.Remove(key); changed = true; }
        }
        return changed;
    }

    /// <summary>True when anything was dropped (so the file is out of date).</summary>
    private bool Prune(DateTimeOffset now)
    {
        var names = Prune(_names, now);
        var guestNames = Prune(_guestNames, now);
        var ips = Prune(_ips, now);
        return names || guestNames || ips;
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
            Restore(state.GuestNames, _guestNames);
            Restore(state.Ips, _ips);
            Prune(_time.GetUtcNow());
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // The file is only a memory of recent failures: start empty rather than refuse to start. Only the exception
            // type is logged, as its message could quote the file.
            _names.Clear();
            _guestNames.Clear();
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
            var state = new State(Snapshot(_names), Snapshot(_ips), Snapshot(_guestNames));
            AtomicFile.WriteAllText(_stateFile, JsonSerializer.Serialize(state, Options));
            _saveFailing = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Once, until a save works again: every failed sign-in would otherwise add a line.
            if (!_saveFailing)
                _log?.LogWarning("The sign-in throttle file {File} could not be saved ({Error}); failures are kept in memory only.",
                    _stateFile, e.GetType().Name);
            _saveFailing = true;
        }
    }

    private static Dictionary<string, SavedEntry> Snapshot(Dictionary<string, Entry> table) =>
        table.ToDictionary(kv => kv.Key, kv => new SavedEntry([.. kv.Value.Failures], kv.Value.LockedUntil == default ? null : kv.Value.LockedUntil),
            table.Comparer);
}
