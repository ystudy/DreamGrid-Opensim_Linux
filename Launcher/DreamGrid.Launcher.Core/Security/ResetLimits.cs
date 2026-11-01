using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// How often a password reset may be asked for, over a rolling hour: at most <see cref="PerAccount"/> reset emails for one
/// account, <see cref="PerEmail"/> to one typed email address (however the name was typed), and <see cref="PerAddress"/>
/// requests from one network address. An IPv4-mapped IPv6 address counts as its IPv4 address; an unknown address (null) counts
/// nothing. Every key is the SHA-256 hex of what it counts (the lower-cased trimmed email, the principal, the address), so the
/// file holds nothing typed. The counts are written to <c>stateFile</c> after each change and read back at construction, so a
/// restart is no fresh start (<see cref="JsonStateFile{T}"/>: a missing or damaged file is an empty start). Safe from any thread.
/// </summary>
public sealed class ResetLimits
{
    public const int PerAccount = 3;
    public const int PerEmail = 3;
    public const int PerAddress = 10;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private sealed record State(Dictionary<string, List<DateTimeOffset>>? Accounts, Dictionary<string, List<DateTimeOffset>>? Emails,
        Dictionary<string, List<DateTimeOffset>>? Addresses);

    private readonly TimeProvider _time;
    private readonly JsonStateFile<State>? _file;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _accounts;
    private readonly Dictionary<string, List<DateTimeOffset>> _emails;
    private readonly Dictionary<string, List<DateTimeOffset>> _addresses;

    public ResetLimits(TimeProvider time, string? stateFile, ILogger<ResetLimits>? log = null)
    {
        _time = time;
        _file = stateFile is null ? null : new JsonStateFile<State>(stateFile, () => new State(null, null, null), log);
        var s = _file?.Load();
        _accounts = new(s?.Accounts ?? [], StringComparer.Ordinal);
        _emails = new(s?.Emails ?? [], StringComparer.Ordinal);
        _addresses = new(s?.Addresses ?? [], StringComparer.Ordinal);
    }

    public bool AddressAllowed(IPAddress? ip)
    {
        if (ip is null) return true;
        lock (_gate) return Count(_addresses, AddressKey(ip)) < PerAddress;
    }

    public void CountAddress(IPAddress? ip)
    {
        if (ip is null) return;
        lock (_gate) Add(_addresses, AddressKey(ip));
    }

    /// <summary>True when neither this account nor this email address has had its reset emails this hour.</summary>
    public bool SendAllowed(Guid principal, string email)
    {
        lock (_gate) return Count(_accounts, Hash("a:" + principal.ToString("N"))) < PerAccount && Count(_emails, EmailKey(email)) < PerEmail;
    }

    public void CountSend(Guid principal, string email)
    {
        lock (_gate)
        {
            Add(_accounts, Hash("a:" + principal.ToString("N")), save: false);
            Add(_emails, EmailKey(email));
        }
    }

    private int Count(Dictionary<string, List<DateTimeOffset>> table, string key)
    {
        if (!table.TryGetValue(key, out var times)) return 0;
        var since = _time.GetUtcNow() - Window;
        return times.Count(t => t > since);
    }

    private void Add(Dictionary<string, List<DateTimeOffset>> table, string key, bool save = true)
    {
        var now = _time.GetUtcNow();
        if (!table.TryGetValue(key, out var times)) table[key] = times = [];
        times.Add(now);
        if (!save) return;
        foreach (var t in new[] { _accounts, _emails, _addresses }) Prune(t, now - Window);
        _file?.Save(new State(_accounts, _emails, _addresses));
    }

    private static void Prune(Dictionary<string, List<DateTimeOffset>> table, DateTimeOffset since)
    {
        foreach (var key in table.Keys.ToList())
        {
            table[key].RemoveAll(t => t <= since);
            if (table[key].Count == 0) table.Remove(key);
        }
    }

    private static string EmailKey(string email) => Hash("e:" + (email ?? "").Trim().ToLowerInvariant());

    private static string AddressKey(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.ScopeId != 0) ip = new IPAddress(ip.GetAddressBytes());
        return Hash("i:" + ip);
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
