using GridGate.Launcher.Core.Tickets;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.People;

/// <summary>
/// One ticket for staff (design 2026-11-05-support-tickets, "A ticket"): the conversation with staff notes, who sent it, and the
/// actions. Every post goes back to the page with a one-time message (post, redirect, get).
/// </summary>
public sealed class TicketModel(StaffTickets tickets) : PageModel
{
    // From the route only: a form field can't point an action at another ticket than the page's.
    [FromRoute] public string? Number { get; set; }
    [BindProperty] public string? Text { get; set; }
    [BindProperty] public bool KeepOpen { get; set; }
    [BindProperty] public string? CaseNumber { get; set; }
    [TempData] public string? Notice { get; set; }
    [TempData] public string? Problem { get; set; }

    public Ticket? Ticket { get; private set; }
    public bool MaySeeTakedowns => StaffTickets.MaySeeTakedowns(User);
    public Guid? Me => AuditActor.Of(User).Id;

    public IActionResult OnGet()
    {
        Ticket = tickets.Get(Number);
        return Ticket is null ? NotFound() : Page();
    }

    public IActionResult OnPostReply() => Done(tickets.Reply(User, Number, Text, KeepOpen, Ip), "Your reply is sent. The guest gets an email with a link to it.");
    public IActionResult OnPostNote() => Done(tickets.Note(User, Number, Text, Ip), "Note added. Only staff can see it.");
    public IActionResult OnPostTake() => Done(tickets.Take(User, Number, over: false, Ip), "You took this ticket.");
    public IActionResult OnPostTakeOver() => Done(tickets.Take(User, Number, over: true, Ip), "You took this ticket over.");
    public IActionResult OnPostLetGo() => Done(tickets.LetGo(User, Number, Ip), "You let this ticket go.");
    public IActionResult OnPostUrgent() => Done(tickets.Urgent(User, Number, true, Ip), "Marked urgent.");
    public IActionResult OnPostNotUrgent() => Done(tickets.Urgent(User, Number, false, Ip), "No longer urgent.");
    public IActionResult OnPostClose() => Done(tickets.Close(User, Number, true, Ip), "Ticket closed. The guest can still reply for 14 days, which opens it again.");
    public IActionResult OnPostReopen() => Done(tickets.Close(User, Number, false, Ip), "Ticket opened again.");
    public IActionResult OnPostLink() => Done(tickets.LinkCase(User, Number, CaseNumber, Ip),
        string.IsNullOrWhiteSpace(CaseNumber) ? "Takedown link removed." : "Linked to the takedown case.");

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private IActionResult Done(string? problem, string notice)
    {
        if (problem is null) Notice = notice;
        else Problem = problem;
        return Redirect("/people/tickets/" + Uri.EscapeDataString(Number ?? ""));
    }
}
