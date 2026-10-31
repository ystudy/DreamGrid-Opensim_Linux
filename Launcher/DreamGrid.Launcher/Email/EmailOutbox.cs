using System.Text.Json;
using DreamGrid.Launcher.Core;
using Microsoft.AspNetCore.DataProtection;

namespace DreamGrid.Launcher.Email;

/// <summary>An email that failed for good: when, which template, whom it was for (when known) and why. No address, body or link.</summary>
public sealed record EmailProblem(DateTimeOffset At, string Template, string? AvatarName, string Reason);

/// <summary>
/// One waiting email. <c>Protected</c> is the recipient and the rendered message, protected by data protection (they hold live
/// links); only the template id and the avatar name are readable, for the recent-problems list.
/// </summary>
public sealed record OutboxEntry(Guid Id, string Template, string? AvatarName, DateTimeOffset Queued, DateTimeOffset Expires, string Protected);

/// <summary>What an <see cref="OutboxEntry"/> protects.</summary>
public sealed record OutboxMessage(string To, string Subject, string Text, string Html);

/// <summary>
/// Email's files in <c>&lt;data&gt;/email</c> besides the accounts: <c>outbox.json</c> (waiting emails, oldest first, at most
/// <see cref="MaxWaiting"/>), <c>sent.json</c> (each account's send times in the last 24 hours) and <c>problems.json</c> (the
/// last 7 days of emails that failed for good). Each is a <see cref="JsonStateFile{T}"/>. Queued messages are protected with
/// purpose <c>DreamGrid.Email.Outbox.v1</c>. Safe to call from any thread.
/// </summary>
public sealed class EmailOutbox
{
    public const int MaxWaiting = 1000;

    public static readonly TimeSpan ProblemsKept = TimeSpan.FromDays(7);

    private const string Purpose = "DreamGrid.Email.Outbox.v1";

    private readonly JsonStateFile<List<OutboxEntry>> _waitingFile;
    private readonly JsonStateFile<Dictionary<Guid, List<DateTimeOffset>>> _sentFile;
    private readonly JsonStateFile<List<EmailProblem>> _problemsFile;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly int _max;
    private readonly Lock _gate = new();
    private readonly List<OutboxEntry> _waiting;
    private readonly Dictionary<Guid, List<DateTimeOffset>> _sent;
    private readonly List<EmailProblem> _problems;

    public EmailOutbox(string folder, IDataProtectionProvider dp, TimeProvider time, int maxWaiting = MaxWaiting, ILogger<EmailOutbox>? log = null)
    {
        _waitingFile = new(Path.Combine(folder, "outbox.json"), () => [], log);
        _sentFile = new(Path.Combine(folder, "sent.json"), () => [], log);
        _problemsFile = new(Path.Combine(folder, "problems.json"), () => [], log);
        _protector = dp.CreateProtector(Purpose);
        _time = time;
        _max = maxWaiting;
        _waiting = [.. _waitingFile.Load().OrderBy(e => e.Queued)];
        _sent = _sentFile.Load();
        _problems = _problemsFile.Load();
    }

    /// <summary>Adds an email at the back. False when <see cref="MaxWaiting"/> are already waiting (or the file can't be written).</summary>
    public bool Add(string template, string? avatarName, OutboxMessage message, DateTimeOffset expires)
    {
        lock (_gate)
        {
            if (_waiting.Count >= _max) return false;
            var json = JsonSerializer.Serialize(message);
            _waiting.Add(new OutboxEntry(Guid.NewGuid(), template, avatarName, _time.GetUtcNow(), expires, _protector.Protect(json)));
            return _waitingFile.Save([.. _waiting]);
        }
    }

    public IReadOnlyList<OutboxEntry> Waiting()
    {
        lock (_gate) return [.. _waiting];
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            if (_waiting.RemoveAll(e => e.Id == id) > 0) _waitingFile.Save([.. _waiting]);
        }
    }

    /// <summary>The protected message, or null when it can't be read (a replaced key ring, a damaged entry).</summary>
    public OutboxMessage? Open(OutboxEntry entry)
    {
        try { return JsonSerializer.Deserialize<OutboxMessage>(_protector.Unprotect(entry.Protected)); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException) { return null; }
    }

    /// <summary>The account's send times in the last 24 hours.</summary>
    public IReadOnlyList<DateTimeOffset> SentBy(Guid account)
    {
        var since = _time.GetUtcNow() - Core.Email.SendWindow.Day;
        lock (_gate) return _sent.TryGetValue(account, out var list) ? [.. list.Where(t => t > since)] : [];
    }

    public void RecordSent(Guid account)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_sent.TryGetValue(account, out var list)) _sent[account] = list = [];
            list.Add(now);
            foreach (var times in _sent.Values) times.RemoveAll(t => t <= now - Core.Email.SendWindow.Day);
            foreach (var empty in _sent.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) _sent.Remove(empty);
            _sentFile.Save(_sent);
        }
    }

    public void AddProblem(string template, string? avatarName, string reason)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _problems.RemoveAll(p => p.At <= now - ProblemsKept);
            _problems.Add(new EmailProblem(now, template, avatarName, reason));
            _problemsFile.Save([.. _problems]);
        }
    }

    /// <summary>The emails that failed for good in the last 7 days, newest first.</summary>
    public IReadOnlyList<EmailProblem> Problems()
    {
        var since = _time.GetUtcNow() - ProblemsKept;
        lock (_gate) return [.. _problems.Where(p => p.At > since).OrderByDescending(p => p.At)];
    }
}
