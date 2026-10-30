using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamGrid.Launcher.Core;

namespace DreamGrid.Launcher.Security;

/// <summary>One signed-in browser of a staff member, as the Sessions page lists it.</summary>
public sealed record StaffSession(string Id, DateTimeOffset Started, DateTimeOffset LastSeen, string? Ip, string? Browser);

/// <summary>
/// Staff members' signed-in browsers and their per-person stamps. A cookie is good only while its session id is listed here
/// and its stamp is the person's current one, so ending a session or signing out everywhere (a new stamp) takes effect on the
/// next request. Kept in <c>file</c> (<c>&lt;data&gt;/security/sessions.json</c>, written atomically) so a restart keeps
/// everyone signed in; a missing or damaged file means nobody is (damaged: one warning). Reading never writes: last-seen
/// times are written at most once a minute per session (<see cref="Touch"/>), and sessions idle for longer than the cookie
/// can live are dropped whenever the file is written. A failed save is logged once; the sessions carry on in memory.
/// Safe to call from any thread.
/// </summary>
public sealed class StaffSessions
{
    /// <summary>How often a session's last-seen time is written.</summary>
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long after its last-seen time a session can still have a live cookie: the cookie's sliding lifetime, plus the
    /// minute last-seen may lag behind.
    /// </summary>
    public static readonly TimeSpan Lifetime = StaffSignIn.Expiry + TouchEvery;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _file;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Person> _people = [];
    private bool _saveFailing;

    public StaffSessions(string file, TimeProvider time, ILogger<StaffSessions>? log = null)
    {
        _file = file;
        _time = time;
        _log = log;
        Load();
    }

    private sealed class Person(string stamp)
    {
        public string Stamp = stamp;
        public readonly List<StaffSession> Sessions = [];
    }

    /// <summary>The saved form.</summary>
    private sealed record State(Dictionary<Guid, SavedPerson>? People);

    private sealed record SavedPerson(string? Stamp, List<StaffSession>? Sessions);

    /// <summary>A new session for <paramref name="principal"/>, listed (and saved) at once.</summary>
    public StaffSession Start(Guid principal, string? ip, string? browser)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var session = new StaffSession(NewToken(), now, now, Text(ip, 64), Text(browser, 300));
            PersonOf(principal).Sessions.Add(session);
            Save();
            return session;
        }
    }

    /// <summary>The person's current stamp (a new person gets one, saved).</summary>
    public string StampOf(Guid principal)
    {
        lock (_gate)
        {
            var known = _people.ContainsKey(principal);
            var stamp = PersonOf(principal).Stamp;
            if (!known) Save();
            return stamp;
        }
    }

    /// <summary>Whether the session is listed, not idle past <see cref="Lifetime"/>, and <paramref name="stamp"/> is current. Never writes.</summary>
    public bool IsValid(Guid principal, string sessionId, string stamp)
    {
        lock (_gate)
        {
            if (!_people.TryGetValue(principal, out var p)) return false;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(p.Stamp), Encoding.UTF8.GetBytes(stamp ?? ""))) return false;
            var now = _time.GetUtcNow();
            return p.Sessions.Any(s => s.Id == sessionId && Live(s, now));
        }
    }

    /// <summary>Moves the session's last-seen time to now, at most once every <see cref="TouchEvery"/>.</summary>
    public void Touch(Guid principal, string sessionId)
    {
        lock (_gate)
        {
            if (!_people.TryGetValue(principal, out var p)) return;
            var at = p.Sessions.FindIndex(s => s.Id == sessionId);
            if (at < 0) return;
            var now = _time.GetUtcNow();
            if (now - p.Sessions[at].LastSeen < TouchEvery) return;
            p.Sessions[at] = p.Sessions[at] with { LastSeen = now };
            Save();
        }
    }

    /// <summary>The person's live sessions, the most recently seen first.</summary>
    public IReadOnlyList<StaffSession> List(Guid principal)
    {
        lock (_gate)
        {
            if (!_people.TryGetValue(principal, out var p)) return [];
            var now = _time.GetUtcNow();
            return [.. p.Sessions.Where(s => Live(s, now)).OrderByDescending(s => s.LastSeen)];
        }
    }

    /// <summary>Ends one session; the person's other sessions and stamp stay.</summary>
    public void End(Guid principal, string sessionId)
    {
        lock (_gate)
        {
            if (_people.TryGetValue(principal, out var p) && p.Sessions.RemoveAll(s => s.Id == sessionId) > 0)
                Save();
        }
    }

    /// <summary>Signs the person out everywhere: no sessions and a new stamp, so no cookie issued before is good.</summary>
    public void EndAll(Guid principal)
    {
        lock (_gate)
        {
            var p = PersonOf(principal);
            p.Sessions.Clear();
            p.Stamp = NewToken();
            Save();
        }
    }

    private Person PersonOf(Guid principal)
    {
        if (!_people.TryGetValue(principal, out var p))
            _people[principal] = p = new Person(NewToken());
        return p;
    }

    private static bool Live(StaffSession s, DateTimeOffset now) => now - s.LastSeen <= Lifetime;

    /// <summary>A random 128-bit value as 32 lower-case hex digits.</summary>
    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string? Text(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : LogText.Clean(value.Trim(), max);

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            string json;
            using (var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();
            var state = JsonSerializer.Deserialize<State>(json, Options) ?? throw new JsonException("the file contains null");
            foreach (var (principal, saved) in state.People ?? [])
            {
                if (saved?.Stamp is not { Length: > 0 } stamp) continue; // without its stamp no cookie of this person can be checked
                var p = new Person(stamp);
                p.Sessions.AddRange((saved.Sessions ?? []).Where(s => s is { Id.Length: > 0 }));
                _people[principal] = p;
            }
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Only the exception type is logged, as its message could quote the file. Everyone signs in again.
            _people.Clear();
            _log?.LogWarning("The staff sessions file {File} could not be read ({Error}); every staff member must sign in again.",
                _file, e.GetType().Name);
        }
    }

    private void Save()
    {
        var now = _time.GetUtcNow();
        foreach (var p in _people.Values)
            p.Sessions.RemoveAll(s => !Live(s, now));
        try
        {
            var state = new State(_people.ToDictionary(kv => kv.Key, kv => new SavedPerson(kv.Value.Stamp, [.. kv.Value.Sessions])));
            AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(state, Options));
            _saveFailing = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Once, until a save works again.
            if (!_saveFailing)
                _log?.LogWarning("The staff sessions file {File} could not be saved ({Error}); sessions are kept in memory only.",
                    _file, e.GetType().Name);
            _saveFailing = true;
        }
    }
}
