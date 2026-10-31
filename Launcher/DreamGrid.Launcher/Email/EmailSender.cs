using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Email;

/// <summary>What <see cref="EmailSender.Queue"/> did with an email.</summary>
public enum QueueResult
{
    Queued,

    /// <summary>No account can send (none set up, all paused or needing attention): nothing was stored.</summary>
    NotReady,

    /// <summary>The queue is full: the email was refused and is a recent problem.</summary>
    Full,
}

/// <summary>One account as the Email page shows it: its counts, and until when it is full or resting (null when it isn't).</summary>
public sealed record AccountStatus(EmailAccount Account, int Hour, int Day, DateTimeOffset? FullUntil, DateTimeOffset? RestingUntil);

/// <summary>Everything the Email page and the dashboard show about sending; <see cref="Warnings"/> are ready-made sentences.</summary>
public sealed record EmailStatus(
    IReadOnlyList<AccountStatus> Accounts,
    int Waiting,
    DateTimeOffset? OldestWaiting,
    IReadOnlyList<EmailProblem> Problems,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The one way into email. A feature calls <see cref="Queue"/>; <see cref="EmailWorker"/> calls <see cref="DrainAsync"/>, which
/// sends the oldest waiting email with the first account that is under its limits (<see cref="SendWindow.Choose"/>) and moves on
/// to the next account when one fails: a refused password marks the account as needing attention, a quota reply makes it full
/// until its window frees, a temporary failure rests it (1, 2, 5, 10, 30 minutes, then 30). An email that expires, or whose
/// recipient is refused, fails for good and becomes a recent problem. An email leaves the outbox only once its outcome is
/// recorded. "Full" and "resting" are kept in memory: after a restart every account is tried again.
/// </summary>
public sealed class EmailSender(
    EmailAccounts accounts,
    EmailOutbox outbox,
    IMailTransport transport,
    Func<LauncherSettings> settings,
    TimeProvider time,
    ILogger<EmailSender> log)
{
    public static readonly TimeSpan DefaultValidFor = TimeSpan.FromHours(24);

    public const string Expired = "Expired before it could be sent";
    public const string Refused = "The receiving server refused the address";
    public const string QueueFull = "The email queue was full";
    public const string Unreadable = "Couldn't read the queued email";
    public const string PasswordUnreadable = "The saved password can't be read. Type it again.";

    public const string WarnFull = "Every mail account is above 80% of its limit.";
    public const string WarnWaiting = "An email has waited more than 10 minutes to be sent.";
    public const string WarnFailed = "An email couldn't be sent in the last 24 hours. See Recent problems.";
    public const string WarnAttention = "A mail account needs attention.";

    private static readonly TimeSpan[] Rests = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _fullUntil = [];
    private readonly Dictionary<Guid, (DateTimeOffset Until, int Times)> _resting = [];
    private readonly SemaphoreSlim _draining = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>Whether some account can send; features show their email links only when it is.</summary>
    public bool IsReady => accounts.IsReady;

    /// <summary>
    /// Renders the template (the owner's wording, <c>{GridName}</c> filled in from settings) and puts it in the outbox, valid for
    /// <paramref name="validFor"/> (24 hours by default).
    /// </summary>
    public QueueResult Queue(string templateId, string to, string? avatarName, IReadOnlyDictionary<string, string> values, TimeSpan? validFor = null)
    {
        if (!accounts.IsReady) return QueueResult.NotReady;
        var s = settings();
        var all = new Dictionary<string, string>(values);
        all.TryAdd("GridName", s.GridName);
        var email = EmailTemplates.Render(templateId, EmailTemplates.Current(s, templateId), all);
        var expires = time.GetUtcNow() + (validFor ?? DefaultValidFor);
        if (!outbox.Add(templateId, avatarName, new OutboxMessage(to, email.Subject, email.Text, email.Html), expires))
        {
            outbox.AddProblem(templateId, avatarName, QueueFull);
            log.LogWarning("Email {Template} to {To} refused: the queue is full", templateId, EmailMask.Of(to));
            return QueueResult.Full;
        }
        _signal.Release();
        return QueueResult.Queued;
    }

    /// <summary>Sends what can be sent now, oldest first; stops when the outbox is empty or no account can send.</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        await _draining.WaitAsync(ct);
        try
        {
            while (await SendNextAsync(ct)) { }
        }
        finally
        {
            _draining.Release();
        }
    }

    /// <summary>Waits until something is queued or <paramref name="timeout"/> passes.</summary>
    public Task WaitForWorkAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    /// <summary>
    /// Sends <paramref name="email"/> straight away with <paramref name="account"/> as typed (maybe unsaved). A send counts against
    /// the account's limits; a failure marks nothing.
    /// </summary>
    public async Task<SendOutcome> SendTestAsync(EmailAccount account, string password, string to, RenderedEmail email, CancellationToken ct)
    {
        var outcome = await transport.SendAsync(account, password, to, email, ct);
        if (outcome.Result == SendResult.Sent) outbox.RecordSent(account.Id);
        log.LogInformation("Test email to {To} via {Provider}: {Result}", EmailMask.Of(to), account.Provider, outcome.Result);
        return outcome;
    }

    public EmailStatus Status()
    {
        var now = time.GetUtcNow();
        var all = accounts.All();
        var rows = all.Select(a =>
        {
            var sent = outbox.SentBy(a.Id);
            var counts = SendWindow.Counts(sent, now);
            DateTimeOffset? full = SendWindow.FreesAt(a, sent, now);
            lock (_gate)
            {
                if (_fullUntil.TryGetValue(a.Id, out var f) && f > now && (full is null || f > full)) full = f;
                var resting = _resting.TryGetValue(a.Id, out var r) && r.Until > now ? r.Until : (DateTimeOffset?)null;
                return new AccountStatus(a, counts.Hour, counts.Day, full, resting);
            }
        }).ToList();
        var waiting = outbox.Waiting();
        var oldest = waiting.Count == 0 ? (DateTimeOffset?)null : waiting.Min(e => e.Queued);
        var problems = outbox.Problems();

        var warnings = new List<string>();
        var usable = rows.Where(r => !r.Account.Paused && r.Account.Problem is null).ToList();
        if (usable.Count > 0 && usable.All(r => SendWindow.Fullness(r.Account, (r.Hour, r.Day)) >= 0.8)) warnings.Add(WarnFull);
        if (oldest is { } o && now - o > TimeSpan.FromMinutes(10)) warnings.Add(WarnWaiting);
        if (problems.Any(p => p.At > now - TimeSpan.FromHours(24))) warnings.Add(WarnFailed);
        if (all.Any(a => a.Problem is not null)) warnings.Add(WarnAttention);
        return new EmailStatus(rows, waiting.Count, oldest, problems, warnings);
    }

    /// <summary>Settles the oldest waiting email: sent, failed for good, or left waiting. True when one was settled.</summary>
    private async Task<bool> SendNextAsync(CancellationToken ct)
    {
        var entry = outbox.Waiting().FirstOrDefault();
        if (entry is null) return false;
        var now = time.GetUtcNow();
        if (entry.Expires <= now)
        {
            Fail(entry, Expired);
            return true;
        }
        if (outbox.Open(entry) is not { } message)
        {
            Fail(entry, Unreadable);
            return true;
        }

        while (true)
        {
            now = time.GetUtcNow();
            var account = SendWindow.Choose(accounts.All(), id => SendWindow.Counts(outbox.SentBy(id), now), id => Unavailable(id, now));
            if (account is null) return false; // it waits for an account to free up
            if (accounts.PasswordOf(account) is not { } password)
            {
                accounts.MarkProblem(account.Id, PasswordUnreadable, now);
                continue;
            }

            var outcome = await transport.SendAsync(account, password, message.To, new RenderedEmail(message.Subject, message.Text, message.Html), ct);
            log.LogInformation("Email {Template} to {To} via {Provider}: {Result}", entry.Template, EmailMask.Of(message.To), account.Provider, outcome.Result);
            switch (outcome.Result)
            {
                case SendResult.Sent:
                    outbox.RecordSent(account.Id);
                    lock (_gate) _resting.Remove(account.Id);
                    outbox.Remove(entry.Id);
                    return true;
                case SendResult.RecipientRefused:
                    Fail(entry, Refused);
                    return true;
                case SendResult.AuthFailed:
                    accounts.MarkProblem(account.Id, outcome.Plain, now);
                    break;
                case SendResult.AccountFull:
                    // Which of its limits the provider counted isn't known: an account with an hourly limit is tried again in an
                    // hour, any other in a day.
                    lock (_gate) _fullUntil[account.Id] = now + (account.PerHour is not null ? SendWindow.Hour : SendWindow.Day);
                    break;
                default:
                    lock (_gate)
                    {
                        var times = _resting.TryGetValue(account.Id, out var r) ? r.Times : 0;
                        _resting[account.Id] = (now + Rests[Math.Min(times, Rests.Length - 1)], times + 1);
                    }
                    break;
            }
        }
    }

    private bool Unavailable(Guid id, DateTimeOffset now)
    {
        lock (_gate)
            return (_fullUntil.TryGetValue(id, out var f) && f > now) || (_resting.TryGetValue(id, out var r) && r.Until > now);
    }

    private void Fail(OutboxEntry entry, string reason)
    {
        outbox.AddProblem(entry.Template, entry.AvatarName, reason);
        outbox.Remove(entry.Id);
        log.LogWarning("Email {Template} failed: {Reason}", entry.Template, reason);
    }
}
