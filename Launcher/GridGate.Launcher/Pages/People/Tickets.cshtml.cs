using GridGate.Launcher.Core.Tickets;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.People;

/// <summary>The staff Tickets list (design 2026-11-05-support-tickets, "The list"): all staff, every ticket.</summary>
public sealed class TicketsModel(StaffTickets tickets) : PageModel
{
    [BindProperty(SupportsGet = true)] public TicketFilter Show { get; set; } = TicketFilter.Open;
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public IReadOnlyList<Ticket> Rows { get; private set; } = [];

    public void OnGet()
    {
        var me = AuditActor.Of(User).Id;
        Rows = tickets.List(Show, Q, me);
    }

    public static string FilterText(TicketFilter f) => f switch
    {
        TicketFilter.Open => "Open",
        TicketFilter.Waiting => "Waiting for the guest",
        TicketFilter.Closed => "Closed",
        TicketFilter.Mine => "Mine",
        _ => "All",
    };

    public static string StateText(TicketState s) => s switch
    {
        TicketState.Open => "Open",
        TicketState.Waiting => "Waiting for the guest",
        TicketState.Closed => "Closed",
        _ => "Not confirmed",
    };

    public static string StateClass(TicketState s) => s switch
    {
        TicketState.Open => "state-running",
        TicketState.Waiting => "state-starting",
        _ => "state-parked",
    };
}
