using System.Security.Claims;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Tickets;
using GridGate.Launcher.Guests;

namespace GridGate.Launcher.Security;

/// <summary>Which tickets the staff list shows.</summary>
public enum TicketFilter { Open, Waiting, Closed, Mine, All }

/// <summary>
/// The staff side of support tickets (design 2026-11-05-support-tickets, "The staff side"). The owner, admins and supervisors all
/// work every ticket; only the owner and admins see or set the takedown link. Every action is checked here again, under the store's
/// lock, and audited with the ticket number and the staff member, never the text.
/// </summary>
public sealed class StaffTickets(TicketStore store, SupportTickets support, TakedownStore takedowns, AuditLog audit, TimeProvider time)
{
    public const string NotFound = "That ticket isn't there any more.";

    /// <summary>The tickets for the list: never unconfirmed ones (staff don't see those), newest activity first.</summary>
    public IReadOnlyList<Ticket> List(TicketFilter filter, string? search, Guid? me)
    {
        var q = (search ?? "").Trim();
        if (q.Length > 120) q = q[..120];
        return [.. store.All().Where(t => t.State != TicketState.Unconfirmed)
            .Where(t => filter switch
            {
                TicketFilter.Open => t.State == TicketState.Open,
                TicketFilter.Waiting => t.State == TicketState.Waiting,
                TicketFilter.Closed => t.State == TicketState.Closed,
                TicketFilter.Mine => t.TakenBy is { } taker && taker == me && t.State != TicketState.Closed,
                _ => true,
            })
            .Where(t => q.Length == 0 || t.Subject.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || t.Sender.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || t.Number.Equals(q, StringComparison.OrdinalIgnoreCase))];
    }

    public Ticket? Get(string? number) =>
        string.IsNullOrEmpty(number) ? null : store.ByNumber(number) is { State: not TicketState.Unconfirmed } t ? t : null;

    /// <summary>Whether <paramref name="actor"/> may see and set the takedown link: the owner and admins (owner, 2026-11-05).</summary>
    public static bool MaySeeTakedowns(ClaimsPrincipal actor) => StaffSignIn.RoleOf(actor) is StaffRole.Owner or StaffRole.Admin;

    /// <summary>A reply to the guest: they get an email with a link (not the text). The ticket waits for them unless <paramref name="keepOpen"/>.</summary>
    public string? Reply(ClaimsPrincipal actor, string? number, string? text, bool keepOpen, string? ip)
    {
        var body = SupportTickets.Text(text);
        if (body.Length < 2) return "Write the reply first.";
        if (body.Length > TicketRules.MessageMax) return $"Keep it under {TicketRules.MessageMax:N0} characters.";
        var (_, id, _) = AuditActor.Of(actor);
        var now = time.GetUtcNow();
        string? problem = null;
        if (Get(number) is null) return NotFound;
        var saved = Change(number, t =>
        {
            if (TooLong(t, body)) { problem = TooLongText; return null; }
            return t with
            {
                State = keepOpen ? TicketState.Open : TicketState.Waiting,
                ClosedAt = null,
                TakenBy = t.TakenBy ?? id,
                TakenByName = t.TakenBy is null ? FullName(actor) : t.TakenByName,
                Messages = [.. t.Messages, new TicketMessage(now, id, FirstName(actor), body)],
            };
        });
        if (saved is null) return problem ?? NotFound;
        Audit(actor, ip, "ticket-reply", saved.Number);
        support.TellGuestStaffReplied(saved);
        return null;
    }

    /// <summary>A staff-only note: never shown to the guest, and it doesn't change the state.</summary>
    public string? Note(ClaimsPrincipal actor, string? number, string? text, string? ip)
    {
        var body = SupportTickets.Text(text);
        if (body.Length < 2) return "Write the note first.";
        if (body.Length > TicketRules.MessageMax) return $"Keep it under {TicketRules.MessageMax:N0} characters.";
        var (_, id, _) = AuditActor.Of(actor);
        var now = time.GetUtcNow();
        if (Get(number) is null) return NotFound;
        var saved = Change(number, t => TooLong(t, body) ? null
            : t with { Messages = [.. t.Messages, new TicketMessage(now, id, FirstName(actor), body, Note: true)] });
        if (saved is null) return TooLongText;
        Audit(actor, ip, "ticket-note", saved.Number);
        return null;
    }

    /// <summary>
    /// Takes a free ticket; <paramref name="over"/> takes one a colleague holds (an explicit "Take it over"). Checked under the
    /// store's lock, so two people taking at once can't both think they have it.
    /// </summary>
    public string? Take(ClaimsPrincipal actor, string? number, bool over, string? ip)
    {
        if (Get(number) is null) return NotFound;
        var (_, id, _) = AuditActor.Of(actor);
        string? problem = null;
        var saved = Change(number, t =>
        {
            if (t.TakenBy == id) { problem = "You already have it."; return null; }
            if (t.TakenBy is not null && !over) { problem = $"{t.TakenByName} already has it. Press Take it over if you mean to."; return null; }
            return t with { TakenBy = id, TakenByName = FullName(actor) };
        });
        if (saved is null) return problem ?? NotFound;
        Audit(actor, ip, over ? "ticket-take-over" : "ticket-take", saved.Number);
        return null;
    }

    /// <summary>Lets a ticket go: the one who holds it, or the owner or an admin.</summary>
    public string? LetGo(ClaimsPrincipal actor, string? number, string? ip)
    {
        if (Get(number) is null) return NotFound;
        var (_, id, _) = AuditActor.Of(actor);
        var boss = MaySeeTakedowns(actor);
        var saved = Change(number, t => t.TakenBy is { } holder && (holder == id || boss) ? t with { TakenBy = null, TakenByName = null } : null);
        if (saved is null) return "Only the one who has it, the owner or an admin can let it go.";
        Audit(actor, ip, "ticket-let-go", saved.Number);
        return null;
    }

    public string? Urgent(ClaimsPrincipal actor, string? number, bool urgent, string? ip)
    {
        if (Get(number) is null) return NotFound;
        var saved = Change(number, t => t with { Urgent = urgent });
        if (saved is null) return NotFound;
        Audit(actor, ip, urgent ? "ticket-urgent" : "ticket-not-urgent", saved.Number);
        return null;
    }

    /// <summary>Closes or reopens. One press: a guest reply within 14 days, or Reopen, undoes a close.</summary>
    public string? Close(ClaimsPrincipal actor, string? number, bool close, string? ip)
    {
        if (Get(number) is null) return NotFound;
        var now = time.GetUtcNow();
        var saved = Change(number, t => close
            ? (t.State == TicketState.Closed ? null : t with { State = TicketState.Closed, ClosedAt = now })
            : (t.State != TicketState.Closed ? null : t with { State = TicketState.Open, ClosedAt = null }));
        if (saved is null) return close ? "It's already closed." : "It's already open.";
        Audit(actor, ip, close ? "ticket-close" : "ticket-reopen", saved.Number);
        return null;
    }

    /// <summary>
    /// Links a Report abuse ticket to a takedown case (owner and admins only), after checking the case exists. An empty case number
    /// removes the link.
    /// </summary>
    public string? LinkCase(ClaimsPrincipal actor, string? number, string? caseNumber, string? ip)
    {
        if (!MaySeeTakedowns(actor)) return "Only the owner and admins can link a takedown case.";
        if (Get(number) is null) return NotFound;
        var wanted = (caseNumber ?? "").Trim().ToUpperInvariant();
        if (wanted.Length > 0 && takedowns.Get(wanted) is null) return $"There's no takedown case {wanted}.";
        var saved = Change(number, t => t.Category != TicketCategories.Abuse ? null : t with { TakedownCase = wanted.Length == 0 ? null : wanted });
        if (saved is null) return "Only a Report abuse ticket can be linked to a takedown case.";
        Audit(actor, ip, "ticket-linked", saved.Number + (wanted.Length == 0 ? "" : " " + wanted));
        return null;
    }

    /// <summary>Tickets linked to a takedown case, for the case page's link back.</summary>
    public IReadOnlyList<Ticket> LinkedTo(string caseNumber) =>
        [.. store.All().Where(t => string.Equals(t.TakedownCase, caseNumber, StringComparison.OrdinalIgnoreCase))];

    private const string TooLongText = "This ticket is too long. Ask the guest to start a new ticket.";

    /// <summary>The same size limits as a guest reply, so staff text can't push a ticket past the point where the guest can answer.</summary>
    private static bool TooLong(Ticket t, string body) =>
        t.Messages.Count >= SupportTickets.MaxMessages || t.Messages.Sum(m => m.Text.Length) + body.Length > SupportTickets.MaxTicketCharacters;

    private Ticket? Change(string? number, Func<Ticket, Ticket?> change) =>
        Get(number) is { } t ? store.Update(t.Id, x => x.State == TicketState.Unconfirmed ? null : change(x)) : null;

    private void Audit(ClaimsPrincipal actor, string? ip, string action, string target)
    {
        var (who, id, role) = AuditActor.Of(actor);
        audit.Write(new AuditEntry(who, id, role, ip, action, target, "ok"));
    }

    private static string FullName(ClaimsPrincipal actor) => AuditActor.Of(actor).Name;

    /// <summary>What the guest sees for a staff message: the staff member's first name only.</summary>
    private static string FirstName(ClaimsPrincipal actor) =>
        (actor.Identity?.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Staff";
}
