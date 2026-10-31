using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The outbox: queued emails go out through the first account with room, move on when one is full or failing, wait
/// (encrypted, across restarts) when none can send, and every email that fails for good is a recent problem.</summary>
public sealed class EmailSenderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Link = "https://grid.example/reset?t=SECRETTOKEN";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-emailsender-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly ScriptedTransport _transport = new();
    private readonly ListLog _log = new();
    private readonly EmailAccounts _accounts;

    public EmailSenderTests()
    {
        Directory.CreateDirectory(_dir);
        _accounts = new EmailAccounts(Path.Combine(_dir, "accounts.json"), _dp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private EmailSender Sender(int maxWaiting = EmailOutbox.MaxWaiting) =>
        new(_accounts, new EmailOutbox(_dir, _dp, _time, maxWaiting), _transport, () => TestSettings.Create() with { GridName = "Avalon" }, _time, _log);

    private EmailAccount Add(string name, int? perDay = 500, int? perHour = null)
    {
        var a = new EmailAccount(Guid.NewGuid(), "gmail", name + ".example", 587, MailSecurity.StartTls, name + "@example.com", null, "Avalon",
            name + "@example.com", null, perDay, perHour, false, 0, null, null);
        _accounts.Save(a, "pw-" + name);
        return _accounts.Find(a.Id)!;
    }

    private static Dictionary<string, string> Reset => new() { ["AvatarName"] = "Ann Avatar", ["Link"] = Link, ["ExpiresIn"] = "30 minutes" };

    private static QueueResult QueueReset(EmailSender s, string to, TimeSpan? validFor = null) =>
        s.Queue(EmailTemplates.ResetPassword, to, "Ann Avatar", Reset, validFor);

    [Fact]
    public void Queue_not_ready_without_accounts()
    {
        var s = Sender();
        Assert.False(s.IsReady);
        Assert.Equal(QueueResult.NotReady, QueueReset(s, "ann@example.com"));
        Assert.Equal(0, s.Status().Waiting);
    }

    [Fact]
    public async Task Sends_with_first_account_and_counts()
    {
        var a = Add("first");
        Add("second");
        var s = Sender();
        Assert.Equal(QueueResult.Queued, QueueReset(s, "ann@example.com"));
        await s.DrainAsync(Ct);
        var sent = Assert.Single(_transport.Sent);
        Assert.Equal(a.Id, sent.Account);
        Assert.Equal("ann@example.com", sent.To);
        Assert.Contains(Link, sent.Email.Text);
        Assert.Equal("Reset your Avalon password", sent.Email.Subject);
        var status = s.Status();
        Assert.Equal(0, status.Waiting);
        Assert.Equal((1, 1), (status.Accounts[0].Hour, status.Accounts[0].Day));
    }

    [Fact]
    public async Task Quota_reply_marks_full_and_moves_on_without_duplicates()
    {
        var a = Add("a");
        var b = Add("b");
        _transport.Script(a.Id, SendResult.Sent, SendResult.Sent, SendResult.AccountFull);
        var s = Sender();
        var to = Enumerable.Range(1, 5).Select(i => $"p{i}@example.com").ToList();
        foreach (var t in to) QueueReset(s, t);
        await s.DrainAsync(Ct);
        var delivered = _transport.Sent.Where(x => x.Result == SendResult.Sent).ToList();
        Assert.Equal(2, delivered.Count(x => x.Account == a.Id));
        Assert.Equal(3, delivered.Count(x => x.Account == b.Id));
        Assert.Equal(to.Order(), delivered.Select(x => x.To).Order());
        Assert.NotNull(s.Status().Accounts.Single(x => x.Account.Id == a.Id).FullUntil);
        Assert.Equal(0, s.Status().Waiting);
    }

    [Fact]
    public async Task Auth_failure_needs_attention_until_saved()
    {
        var a = Add("a");
        var b = Add("b");
        _transport.Script(a.Id, SendResult.AuthFailed);
        var s = Sender();
        QueueReset(s, "ann@example.com");
        await s.DrainAsync(Ct);
        Assert.Equal(b.Id, _transport.Sent.Single(x => x.Result == SendResult.Sent).Account);
        Assert.Equal("The server refused the password", _accounts.Find(a.Id)!.Problem);
        Assert.Contains("A mail account needs attention.", s.Status().Warnings);
        _accounts.Save(_accounts.Find(a.Id)!, "new-password");
        QueueReset(s, "bob@example.com");
        await s.DrainAsync(Ct);
        Assert.Equal(a.Id, _transport.Sent.Last().Account);
    }

    [Fact]
    public async Task Temporary_failure_rests_account_then_retries()
    {
        var a = Add("a");
        _transport.Script(a.Id, SendResult.Temporary);
        var s = Sender();
        QueueReset(s, "ann@example.com");
        await s.DrainAsync(Ct);
        Assert.Equal(1, s.Status().Waiting);
        Assert.NotNull(s.Status().Accounts[0].RestingUntil);
        _time.Advance(TimeSpan.FromSeconds(30));
        await s.DrainAsync(Ct);
        Assert.Single(_transport.Sent); // still resting
        _time.Advance(TimeSpan.FromMinutes(1));
        await s.DrainAsync(Ct);
        Assert.Equal(SendResult.Sent, _transport.Sent.Last().Result);
        Assert.Equal(0, s.Status().Waiting);
        Assert.Null(_accounts.Find(a.Id)!.Problem);
    }

    [Fact]
    public async Task Recipient_refused_is_a_problem_not_retried()
    {
        var a = Add("a");
        Add("b");
        _transport.Script(a.Id, SendResult.RecipientRefused);
        var s = Sender();
        QueueReset(s, "who@nowhere.example");
        await s.DrainAsync(Ct);
        Assert.Single(_transport.Sent);
        var problem = Assert.Single(s.Status().Problems);
        Assert.Equal("Ann Avatar", problem.AvatarName);
        Assert.Equal(EmailTemplates.ResetPassword, problem.Template);
        Assert.Equal("The receiving server refused the address", problem.Reason);
        Assert.Contains("An email couldn't be sent in the last 24 hours. See Recent problems.", s.Status().Warnings);
    }

    [Fact]
    public async Task Queue_survives_restart_and_drops_expired()
    {
        var a = Add("a");
        _transport.Script(a.Id, SendResult.Temporary);
        var first = Sender();
        QueueReset(first, "short@example.com", TimeSpan.FromMinutes(30));
        QueueReset(first, "long@example.com", TimeSpan.FromHours(5));
        await first.DrainAsync(Ct);
        _time.Advance(TimeSpan.FromHours(1));

        var second = Sender(); // the launcher restarted
        Assert.Equal(2, second.Status().Waiting);
        await second.DrainAsync(Ct);
        Assert.Equal("long@example.com", _transport.Sent.Last(x => x.Result == SendResult.Sent).To);
        Assert.DoesNotContain(_transport.Sent, x => x.To == "short@example.com" && x.Result == SendResult.Sent);
        Assert.Equal("Expired before it could be sent", Assert.Single(second.Status().Problems).Reason);
        Assert.Equal(0, second.Status().Waiting);
    }

    [Fact]
    public async Task Outbox_file_has_no_address_or_link()
    {
        var a = Add("a");
        _transport.Script(a.Id, SendResult.Temporary);
        var s = Sender();
        QueueReset(s, "ann@example.com");
        await s.DrainAsync(Ct);
        foreach (var file in Directory.GetFiles(_dir).Where(f => !f.EndsWith("accounts.json", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ann@example.com", text);
            Assert.DoesNotContain("SECRETTOKEN", text);
        }
    }

    [Fact]
    public void Full_queue_refuses_and_records_problem()
    {
        Add("a");
        var s = Sender(maxWaiting: 3);
        for (var i = 0; i < 3; i++)
            Assert.Equal(QueueResult.Queued, QueueReset(s, $"p{i}@example.com"));
        Assert.Equal(QueueResult.Full, QueueReset(s, "one-more@example.com"));
        Assert.Equal("The email queue was full", Assert.Single(s.Status().Problems).Reason);
        Assert.Equal(1000, EmailOutbox.MaxWaiting);
    }

    [Fact]
    public async Task Problems_older_than_seven_days_are_dropped()
    {
        var a = Add("a");
        _transport.Script(a.Id, SendResult.RecipientRefused, SendResult.RecipientRefused);
        var s = Sender();
        QueueReset(s, "old@example.com");
        await s.DrainAsync(Ct);
        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        QueueReset(s, "new@example.com");
        await s.DrainAsync(Ct);
        Assert.Single(s.Status().Problems);
        Assert.Single(Sender().Status().Problems);
    }

    [Fact]
    public async Task Warnings_cover_the_four_conditions()
    {
        var s = Sender();
        Assert.Empty(s.Status().Warnings);
        var a = Add("a", perDay: 10);
        for (var i = 0; i < 8; i++) QueueReset(s, $"p{i}@example.com");
        await s.DrainAsync(Ct);
        Assert.Contains("Every mail account is above 80% of its limit.", s.Status().Warnings);

        _transport.Script(a.Id, SendResult.Temporary);
        QueueReset(s, "late@example.com");
        await s.DrainAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Contains("An email has waited more than 10 minutes to be sent.", s.Status().Warnings);
    }

    [Fact]
    public async Task Test_send_counts_but_marks_no_problem()
    {
        var a = Add("a");
        _transport.Script(a.Id, SendResult.Sent, SendResult.AuthFailed);
        var s = Sender();
        var email = new RenderedEmail("t", "t", "t");
        Assert.Equal(SendResult.Sent, (await s.SendTestAsync(a, "pw-a", "me@example.com", email, Ct)).Result);
        Assert.Equal(1, s.Status().Accounts[0].Day);
        Assert.Equal(SendResult.AuthFailed, (await s.SendTestAsync(a, "bad", "me@example.com", email, Ct)).Result);
        Assert.Null(_accounts.Find(a.Id)!.Problem);
        Assert.Equal(1, s.Status().Accounts[0].Day);
    }

    [Fact]
    public async Task Log_line_masks_recipient()
    {
        Add("a");
        var s = Sender();
        QueueReset(s, "ann@example.com");
        await s.DrainAsync(Ct);
        Assert.Contains(_log.Lines, l => l.Contains("a…@example.com", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("ann@example.com", StringComparison.Ordinal) || l.Contains("SECRETTOKEN", StringComparison.Ordinal));
    }

    /// <summary>Answers each account's sends with its scripted results in turn, then <see cref="SendResult.Sent"/>.</summary>
    private sealed class ScriptedTransport : IMailTransport
    {
        private readonly Dictionary<Guid, Queue<SendResult>> _script = [];

        public List<(Guid Account, string To, RenderedEmail Email, SendResult Result)> Sent { get; } = [];

        public void Script(Guid account, params SendResult[] results) => _script[account] = new Queue<SendResult>(results);

        public Task<SendOutcome> SendAsync(EmailAccount account, string password, string to, RenderedEmail email, CancellationToken ct)
        {
            var result = _script.TryGetValue(account.Id, out var q) && q.Count > 0 ? q.Dequeue() : SendResult.Sent;
            Sent.Add((account.Id, to, email, result));
            return Task.FromResult(new SendOutcome(result, result switch
            {
                SendResult.AuthFailed => "The server refused the password",
                SendResult.AccountFull => "The account has reached its sending limit",
                SendResult.RecipientRefused => "The server refused this address",
                SendResult.Temporary => "Couldn't reach the server",
                _ => "Signed in and sent",
            }));
        }
    }

    private sealed class ListLog : ILogger<EmailSender>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
