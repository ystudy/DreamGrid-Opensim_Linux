using System.Net;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Tickets;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Email;

namespace DreamGrid.Launcher.Guests;

/// <summary>What a guest typed into the ticket form.</summary>
public sealed record TicketForm(string? Name, string? Email, string? Category, string? Subject, string? Message, string? Evidence, string? Logs);

public enum TicketSendOutcome { Opened, CheckEmail, Invalid, TooManyOpen, Full, EmailOff, Busy }

/// <param name="Ticket">The ticket, when it was opened at once (a signed-in resident).</param>
/// <param name="Problem">The problem in English, for logs and tests.</param>
/// <param name="Message">The same problem for the page, in the resident's language (<see cref="ResidentMessage"/>).</param>
public sealed record TicketSendResult(TicketSendOutcome Outcome, string? Problem = null, Ticket? Ticket = null, ResidentMessage? Message = null);

/// <summary>
/// The guest side of support tickets (design 2026-11-05-support-tickets): sending, confirming by email, private links, the guest's
/// replies and the lost-link page. [S] Its answers never say whether an address has an account or tickets; its limits are kept in
/// memory (a restart forgets them, as the other guest limits do).
/// </summary>
public sealed class SupportTickets(TicketStore store, EmailTokens tokens, EmailSender email, Func<LauncherSettings> settings,
    AuditLog audit, TimeProvider time, EmailLanguage emailLanguage, ILogger<SupportTickets> log)
{
    public const int MaxOpenPerSender = 3;
    public const int MaxOpenInAll = 2000;
    public const int MaxMessages = 200;
    /// <summary>All the text on one ticket, so a file never grows past a few hundred KB.</summary>
    public const int MaxTicketCharacters = 200_000;
    /// <summary>Tickets waiting for their sender to confirm, across every address.</summary>
    public const int MaxUnconfirmed = 500;
    public const int EmailsPerAddressPerDay = 3;
    public static readonly TimeSpan ConfirmLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan ReopenWithin = TimeSpan.FromDays(14);
    public const string ConfirmPurpose = "ticket-confirm";
    public const string ConfirmPath = "/support/confirm";
    public const string TicketPath = "/support/ticket";

    public const string TooManyOpen = "You already have 3 open tickets. Reply on one of them instead.";
    public const string Full = "The grid isn't taking new tickets right now. Try again later.";
    public const string EmailOff = "Sign in to send a ticket. This grid can't send email yet, so it can't check an address.";
    public const string Busy = "Your ticket couldn't be saved. Try again in a few minutes.";
    public const string ClosedForGood = "This ticket is closed. Start a new ticket.";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _sent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether someone not signed in can send a ticket: only when the grid can email them a confirmation.</summary>
    public bool AnonymousOpen => email.IsReady;

    /// <summary>
    /// Sends a ticket. <paramref name="account"/> is the signed-in resident (their name and email come from their account), or null
    /// for someone not signed in, whose ticket waits for them to confirm their email. The emails go out in <paramref name="language"/>,
    /// the language of the page (the sender may have no account, so no saved language; task 10 keeps it on the ticket).
    /// </summary>
    public TicketSendResult Send(TicketForm form, RobustAccount? account, IPAddress? ip, string language = Languages.English)
    {
        var now = time.GetUtcNow();
        var anonymous = account is null;
        if (anonymous && !email.IsReady) return new(TicketSendOutcome.EmailOff, EmailOff, Message: new("Support.EmailOff"));
        var name = anonymous ? Line(form.Name) : $"{account!.FirstName} {account.LastName}";
        // [S] One address, one way of writing it: "x <a@b.c>" and "A@B.C" are a@b.c, so no limit can be got round by rewriting it.
        var address = anonymous ? TicketRules.Address(form.Email) ?? "" : (account!.Email ?? "").Trim().ToLowerInvariant();
        if (TicketRules.Check(form, name, address, anonymous) is var (problem, message)) return new(TicketSendOutcome.Invalid, problem, Message: message);
        // Unconfirmed tickets older than their link are gone now, not only at the next sweep, so they never count against the limits.
        store.DeleteWhere(t => t.State == TicketState.Unconfirmed && now - t.Created > ConfirmLifetime);

        var sender = new TicketSender(account?.PrincipalId, name, address);
        var refused = TicketSendOutcome.Opened;
        var ticket = store.Create(all =>
        {
            if (all.Count(t => t.IsActive) >= MaxOpenInAll) { refused = TicketSendOutcome.Full; return false; }
            if (anonymous && all.Count(t => t.State == TicketState.Unconfirmed) >= MaxUnconfirmed) { refused = TicketSendOutcome.Full; return false; }
            // [S] Someone not signed in is never told what an address already has: their ticket just isn't kept or mailed.
            if (all.Count(t => (t.IsActive || t.State == TicketState.Unconfirmed) && SameSender(t.Sender, sender)) >= MaxOpenPerSender)
            {
                refused = anonymous ? TicketSendOutcome.CheckEmail : TicketSendOutcome.TooManyOpen;
                return false;
            }
            // [S] Past the day's emails to this address nothing is kept either, so a stranger can't fill the store.
            if (anonymous && !Allowed(address, now)) { refused = TicketSendOutcome.CheckEmail; return false; }
            return true;
        }, (id, number) => new Ticket(id, number, anonymous ? TicketState.Unconfirmed : TicketState.Open,
            form.Category!, Line(form.Subject), sender, now, anonymous ? null : now, null,
            [new TicketMessage(now, null, null, Text(form.Message))], [],
            Evidence: string.IsNullOrWhiteSpace(form.Evidence) ? null : form.Evidence.Trim(),
            Logs: string.IsNullOrWhiteSpace(form.Logs) ? null : Text(form.Logs)));

        if (ticket is null)
            return refused switch
            {
                TicketSendOutcome.CheckEmail => new(TicketSendOutcome.CheckEmail),
                TicketSendOutcome.Full => new(TicketSendOutcome.Full, Full, Message: new("Support.Full")),
                TicketSendOutcome.TooManyOpen => new(TicketSendOutcome.TooManyOpen, TooManyOpen, Message: new("Support.TooManyOpen", MaxOpenPerSender)),
                _ => new(TicketSendOutcome.Busy, Busy, Message: new("Support.Busy")),
            };

        audit.Write(new AuditEntry(name, account?.PrincipalId, "guest", ip?.ToString(), "ticket-sent", ticket.Number, anonymous ? "unconfirmed" : "ok"));
        if (anonymous)
        {
            var token = tokens.Issue(ConfirmPurpose, ticket.Id, ConfirmLifetime);
            Queue(EmailTemplates.TicketConfirm, language, address, name, ticket, Website() + ConfirmPath + "?token=" + token, ConfirmLifetime);
            return new(TicketSendOutcome.CheckEmail);
        }
        SendAccess(ticket, language);
        return new(TicketSendOutcome.Opened, Ticket: store.Get(ticket.Id));
    }

    /// <summary>
    /// A confirmation link: the ticket opens, and its private link is emailed. Null for a used, expired or unknown link, or when
    /// opening it now would pass the open-ticket limits (checked again here, at the moment it opens).
    /// </summary>
    public Ticket? Confirm(string? token, string language = Languages.English)
    {
        // Find names the ticket; TryRedeem spends the token, once, for that ticket only.
        if (tokens.Find(ConfirmPurpose, token) is not { } id || !tokens.TryRedeem(ConfirmPurpose, id, token)) return null;
        var now = time.GetUtcNow();
        var ticket = store.Update(id, (all, t) =>
            t.State == TicketState.Unconfirmed
            && now - t.Created <= ConfirmLifetime
            && all.Count(x => x.IsActive) < MaxOpenInAll
            && all.Count(x => x.IsActive && SameSender(x.Sender, t.Sender)) < MaxOpenPerSender
                ? t with { State = TicketState.Open, Confirmed = now }
                : null);
        if (ticket is null) return null;
        audit.Write(new AuditEntry(ticket.Sender.Name, null, "guest", null, "ticket-confirmed", ticket.Number, "ok"));
        SendAccess(ticket, language);
        return store.Get(id);
    }

    /// <summary>The ticket a private link opens, or null (an unconfirmed ticket is never opened by a link).</summary>
    public Ticket? Open(Guid id, string? link)
    {
        var t = store.Get(id);
        return t is not null && t.State != TicketState.Unconfirmed && TicketLinks.Opens(t, link, time.GetUtcNow()) ? t : null;
    }

    /// <summary>The ticket <paramref name="id"/> for a guest who holds its cookie or owns it (signed-in resident), or null.</summary>
    public Ticket? ForGuest(Guid id) => store.Get(id) is { State: not TicketState.Unconfirmed } t ? t : null;

    /// <summary>A signed-in resident's own tickets, newest activity first.</summary>
    public IReadOnlyList<Ticket> OfAccount(Guid account) =>
        [.. store.All().Where(t => t.Sender.Account == account && t.State != TicketState.Unconfirmed)];

    /// <summary>Whether the guest can still answer: open or waiting, or closed less than 14 days ago.</summary>
    public bool CanReply(Ticket t) =>
        t.State is TicketState.Open or TicketState.Waiting
        || (t.State == TicketState.Closed && t.ClosedAt is { } closed && time.GetUtcNow() - closed < ReopenWithin);

    /// <summary>The guest's reply: the ticket is open again (reopened if it was closed less than 14 days ago). The problem for the page, or null.</summary>
    public ResidentMessage? Reply(Guid id, string? text, IPAddress? ip)
    {
        var body = Text(text);
        if (body.Length < TicketRules.MessageMin) return new("Support.ReplyShort", TicketRules.MessageMin);
        if (body.Length > TicketRules.MessageMax) return new("Support.ReplyLong", TicketRules.MessageMax);
        var now = time.GetUtcNow();
        ResidentMessage? problem = null;
        var saved = store.Update(id, t =>
        {
            if (!CanReply(t)) { problem = new("Support.ClosedForGood"); return null; }
            if (t.Messages.Count >= MaxMessages || t.Messages.Sum(m => m.Text.Length) + body.Length > MaxTicketCharacters)
            { problem = new("Support.TooLong"); return null; }
            return t with { State = TicketState.Open, ClosedAt = null, Messages = [.. t.Messages, new TicketMessage(now, null, null, body)] };
        });
        if (saved is null) return problem ?? new("Support.Busy");
        audit.Write(new AuditEntry(saved.Sender.Name, saved.Sender.Account, "guest", ip?.ToString(), "ticket-guest-reply", saved.Number, "ok"));
        return null;
    }

    /// <summary>
    /// "I lost my link": a new link for each of the address's open tickets or ones closed in the last 14 days. [S] The caller shows
    /// the same reply whatever happens here; at most 3 such emails per address a day.
    /// </summary>
    public void SendLostLinks(string? address, string language = Languages.English)
    {
        if (TicketRules.Address(address) is not { } to) return;
        // [S] In the background, so the page answers in the same time whether or not the address has tickets.
        _ = Task.Run(() =>
        {
            try
            {
                var mine = store.All().Where(t => t.State != TicketState.Unconfirmed && string.Equals(t.Sender.Email, to, StringComparison.OrdinalIgnoreCase)
                    && (t.IsActive || CanReply(t))).ToList();
                // Each email counts against the address's 3 a day, so many tickets can't mean many emails.
                foreach (var t in mine)
                {
                    if (!Allowed(to, time.GetUtcNow())) break;
                    SendAccess(t, language);
                }
            }
            catch (Exception e)
            {
                log.LogWarning("Support tickets: new links couldn't be sent ({Error}).", e.GetType().Name);
            }
        });
    }

    /// <summary>
    /// Staff answered: the sender gets an email with a fresh private link, never the reply's text. In the grid's default language
    /// for now: the ticket's own language comes with task 10.
    /// </summary>
    public void TellGuestStaffReplied(Ticket ticket)
    {
        // No email can go out: don't use up one of the ticket's five live links for nothing.
        if (!email.IsReady) return;
        SendAccess(ticket, emailLanguage.Default(), EmailTemplates.TicketStaffReply);
    }

    /// <summary>Adds a fresh private link to the ticket and emails it to the sender.</summary>
    private void SendAccess(Ticket ticket, string language, string template = EmailTemplates.TicketAccess)
    {
        string? link = null;
        var saved = store.Update(ticket.Id, t =>
        {
            var (next, made) = TicketLinks.Add(t, time.GetUtcNow());
            link = made;
            return next;
        });
        if (saved is null || link is null || string.IsNullOrEmpty(saved.Sender.Email)) return;
        Queue(template, language, saved.Sender.Email, saved.Sender.Name, saved, Website() + TicketPath + "?id=" + saved.Id + "&link=" + link,
            TimeSpan.FromHours(24));
    }

    private void Queue(string template, string language, string to, string name, Ticket t, string link, TimeSpan validFor)
    {
        var result = email.Queue(template, language, to, name, new Dictionary<string, string>
        {
            ["Name"] = name, ["TicketNumber"] = t.Number, ["Subject"] = t.Subject, ["Link"] = link,
        }, validFor);
        if (result != QueueResult.Queued)
            log.LogWarning("Support ticket {Number}: the {Template} email couldn't be queued: {Result}", t.Number, template, result);
    }

    /// <summary>At most <see cref="EmailsPerAddressPerDay"/> emails to one address a day; counts this one when allowed.</summary>
    private bool Allowed(string address, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_sent.TryGetValue(address, out var times)) _sent[address] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && times.Peek() <= now - TimeSpan.FromDays(1)) times.Dequeue();
            if (times.Count >= EmailsPerAddressPerDay) return false;
            times.Enqueue(now);
            // Addresses whose day has passed are forgotten, so the list doesn't grow for ever.
            if (_sent.Count > 1000)
                foreach (var stale in _sent.Where(kv => kv.Value.Count == 0 || kv.Value.Last() <= now - TimeSpan.FromDays(1)).Select(kv => kv.Key).ToList())
                    _sent.Remove(stale);
            return true;
        }
    }

    private string Website() => WebsiteAddress.Base(settings());

    private static bool SameSender(TicketSender a, TicketSender b) =>
        (a.Account is { } x && b.Account is { } y) ? x == y : string.Equals(a.Email, b.Email, StringComparison.OrdinalIgnoreCase);

    /// <summary>One line for a name or subject: no line breaks, control or direction-changing characters (they'd reach emails and logs).</summary>
    internal static string Line(string? s) =>
        new string((s ?? "").Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();

    /// <summary>Line endings as \n, no other control characters, trimmed.</summary>
    internal static string Text(string? s) =>
        new string((s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Where(c => c == '\n' || c == '\t' || (!char.IsControl(c) && !IsDirection(c))).ToArray()).Trim();

    /// <summary>The characters that change the direction text is shown in, which could make a message look different to staff.</summary>
    private static bool IsDirection(char c) => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u061C';
}

/// <summary>The ticket form's rules (design 2026-11-05-support-tickets, "Sending a ticket").</summary>
public static class TicketRules
{
    public const int NameMax = 64;
    public const int EmailMax = 254;
    public const int SubjectMin = 3;
    public const int SubjectMax = 120;
    public const int MessageMin = 10;
    public const int MessageMax = 8000;
    public const int EvidenceMax = 500;
    public const int LogsMax = 20000;

    /// <summary>
    /// [S] An email address as one plain address, lower-cased: null for anything else, including "Name &lt;a@b.c&gt;" forms, which
    /// the mail library would send to a@b.c, so every limit is keyed on the address the email really goes to.
    /// </summary>
    public static string? Address(string? typed)
    {
        var t = (typed ?? "").Trim();
        if (t.Length is 0 or > EmailMax || t.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return null;
        return MimeKit.MailboxAddress.TryParse(t, out var parsed) && string.Equals(parsed.Address, t, StringComparison.OrdinalIgnoreCase)
            && parsed.Address.Contains('@') ? parsed.Address.ToLowerInvariant() : null;
    }

    /// <summary>The first problem with the form, in plain words (English, for logs and tests), or null.</summary>
    public static string? Problem(TicketForm f, string name, string email, bool anonymous) =>
        Check(f, name, email, anonymous)?.English;

    /// <summary>The first problem with the form: its English words, and the same for the page in the resident's language.</summary>
    public static (string English, ResidentMessage Message)? Check(TicketForm f, string name, string email, bool anonymous)
    {
        if (anonymous && (name.Length == 0 || name.Length > NameMax))
            return ($"Write your name, up to {NameMax} characters.", new("Support.NameProblem", NameMax));
        if (anonymous && email.Length == 0)
            return ("Write the email address we should answer.", new("Support.EmailProblem"));
        if (f.Category is null || !TicketCategories.All.Contains(f.Category))
            return ("Pick what your ticket is about.", new("Support.CategoryProblem"));
        var subject = SupportTickets.Line(f.Subject);
        if (subject.Length < SubjectMin || subject.Length > SubjectMax)
            return ($"Write a subject of {SubjectMin} to {SubjectMax} characters.", new("Support.SubjectProblem", SubjectMin, SubjectMax));
        var message = SupportTickets.Text(f.Message);
        if (message.Length < MessageMin)
            return ($"Write at least {MessageMin} characters about the problem.", new("Support.MessageShort", MessageMin));
        if (message.Length > MessageMax)
            return ($"Keep the message under {MessageMax:N0} characters.", new("Support.MessageLong", MessageMax));
        if (!string.IsNullOrWhiteSpace(f.Evidence))
        {
            var e = f.Evidence.Trim();
            if (e.Length > EvidenceMax || !Uri.TryCreate(e, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                return ("The evidence link must be one web address starting with https:// or http://.", new("Support.EvidenceProblem"));
        }
        if ((f.Logs ?? "").Length > LogsMax)
            return ($"Keep the logs under {LogsMax:N0} characters.", new("Support.LogsProblem", LogsMax));
        return null;
    }
}
