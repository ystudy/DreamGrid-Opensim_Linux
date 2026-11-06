using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Tickets;
using DreamGrid.Launcher.Email;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Support tickets' keeping and notices (design 2026-11-05-support-tickets, "Notices" and "Keeping"), every <see cref="Period"/>:
/// <list type="bullet">
/// <item>deletes unconfirmed tickets after <see cref="SupportTickets.ConfirmLifetime"/>, and closed ones <see cref="KeepClosed"/> after
/// closing; a ticket linked to a takedown case also waits until the case has been closed that long, so while a case is open (or
/// can't be read) its tickets stay;</item>
/// <item>emails staff who switched on "New support tickets" (<see cref="TicketNotices"/>): <see cref="EmailTemplates.TicketNew"/> names
/// tickets that opened since their last email, <see cref="EmailTemplates.TicketReplyGuest"/> tickets a guest answered. A reply on
/// a taken ticket goes to whoever took it; on one nobody holds, to everyone with the switch on. At most one of each every
/// <see cref="MinGap"/> per staff member (so up to two in one go); what comes in meanwhile is named together in the next. [S] Numbers, categories and
/// subjects only, never a message or the guest's email.</item>
/// </list>
/// The pass runs every few minutes, so the first notice comes soon; the hour is counted per person.
/// </summary>
public sealed class TicketSweep(TicketStore store, TicketNotices notices, TakedownStore takedowns, IStaffDirectory directory, IRobustClient robust,
    EmailSender sender, Func<LauncherSettings> settings, AuditLog audit, TimeProvider time, ILogger<TicketSweep> log) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MinGap = TimeSpan.FromHours(1);
    public static readonly TimeSpan KeepClosed = TimeSpan.FromDays(365);
    /// <summary>Tickets named in one email; the rest are counted.</summary>
    public const int MaxNamed = 20;
    public const string ListPath = "/people/tickets";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            using var timer = new PeriodicTimer(Period, time);
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Support tickets: the clean-up or the staff notices failed: {Message}", e.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>One pass (the loop's work; tests call it directly). Passes run one at a time.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            Sweep(now);
            await NotifyAsync(now, ct);
        }
        finally { _one.Release(); }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var t in store.DeleteWhere(t => Expired(t, now)))
            audit.Write(new AuditEntry("DreamGrid", null, "system", null, "ticket-deleted", t.Number,
                t.State == TicketState.Unconfirmed ? "unconfirmed" : "closed a year"));
    }

    private bool Expired(Ticket t, DateTimeOffset now)
    {
        if (t.State == TicketState.Unconfirmed) return now - t.Created > SupportTickets.ConfirmLifetime;
        if (t.State != TicketState.Closed || t.ClosedAt is not { } closed || now - closed <= KeepClosed) return false;
        if (t.TakedownCase is not { } number) return true;
        // Kept while the case is open, or when it can't be read: a deleted ticket can't come back.
        return takedowns.Get(number) is { Open: false } c
            && c.Events.LastOrDefault(e => e.What == "closed") is { } done
            && now - done.At > KeepClosed;
    }

    private async Task NotifyAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = settings();
        if (!sender.IsReady || !AdminRule.HasStaffPages(s)) return;
        var on = notices.All();
        if (on.Count == 0) return;
        var due = on.Where(kv => kv.Value.LastSent is not { } last || now - last >= MinGap).ToList();
        if (due.Count == 0) return;

        List<RobustAccount> staff;
        try
        {
            staff = [.. await directory.ListAtOrAboveAsync(s.Security.SupervisorLevel, ct)];
            if (s.Setup.Owner is { } o && staff.All(a => a.PrincipalId != o.PrincipalId)
                && await robust.FindAccountByIdAsync(o.PrincipalId, ct) is { } owner)
                staff.Add(owner);
        }
        catch (Exception e) when (e is StaffDirectoryUnavailableException or HttpRequestException or TaskCanceledException or IOException
            or InvalidOperationException or InvalidDataException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Support tickets: the staff list couldn't be read, notices wait for the next pass: {Message}", e.InnerException?.Message ?? e.Message);
            return;
        }
        // Staff today: someone who lost their role gets nothing, and a ticket they still hold counts as nobody's.
        var current = staff.Where(a => AdminRule.RoleOf(s, a) != StaffRole.None).ToDictionary(a => a.PrincipalId);
        var tickets = store.All().Where(t => t.State != TicketState.Unconfirmed).ToList();
        var link = WebsiteAddress.Base(s) + ListPath;

        foreach (var (id, entry) in due)
        {
            // No longer staff: off for good, so their id isn't kept and switching back on starts fresh.
            if (!current.TryGetValue(id, out var account)) { notices.Set(id, false, now); continue; }
            // No address: nothing can be told, and nothing piles up for when one is added.
            if (account.Email.Trim().Length == 0) { notices.Skip(id, now); continue; }
            bool Since(DateTimeOffset at) => at > entry.Covered && at <= now;
            var opened = tickets.Where(t => t.Confirmed is { } c && Since(c)).OrderBy(t => t.Confirmed).ToList();
            var answered = tickets.Where(t => !opened.Contains(t)
                    && t.Messages.Skip(1).Any(m => m.Staff is null && Since(m.At))
                    && (t.TakenBy is not { } holder || holder == id || !current.ContainsKey(holder)))
                .OrderBy(t => t.LastActivity).ToList();
            if (opened.Count == 0 && answered.Count == 0) continue;

            var to = account.Email.Trim();
            var name = $"{account.FirstName} {account.LastName}";
            var queued = (opened.Count == 0 || Queue(EmailTemplates.TicketNew, to, name, opened, link))
                & (answered.Count == 0 || Queue(EmailTemplates.TicketReplyGuest, to, name, answered, link));
            // An email that couldn't be queued (the outbox is full) is tried again next pass, with what came in meanwhile.
            if (queued) notices.Sent(id, now, now);
        }
    }

    private bool Queue(string template, string to, string name, List<Ticket> list, string link)
    {
        var lines = list.Take(MaxNamed).Select(t => $"{t.Number} ({TicketCategories.Title(t.Category)}): {t.Subject}").ToList();
        if (list.Count > MaxNamed) lines.Add($"and {list.Count - MaxNamed} more");
        var result = sender.Queue(template, to, name, new Dictionary<string, string>
        {
            ["Count"] = list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Numbers"] = string.Join(", ", list.Take(MaxNamed).Select(t => t.Number)) + (list.Count > MaxNamed ? ", …" : ""),
            ["Tickets"] = string.Join("\n", lines),
            ["Link"] = link,
        }, TimeSpan.FromHours(24));
        if (result == QueueResult.Queued) return true;
        log.LogWarning("Support tickets: the {Template} notice couldn't be queued: {Result}", template, result);
        return false;
    }
}
