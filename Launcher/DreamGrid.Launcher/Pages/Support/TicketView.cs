using DreamGrid.Launcher.Core.Tickets;

namespace DreamGrid.Launcher.Pages.Support;

/// <summary>How the guest pages word a ticket.</summary>
public static class TicketView
{
    public static string StateText(TicketState s) => s switch
    {
        TicketState.Open => "Open",
        TicketState.Waiting => "Waiting for you",
        TicketState.Closed => "Closed",
        _ => "Not sent yet",
    };

    /// <summary>The messages a guest may see: never a staff-only note.</summary>
    public static IEnumerable<TicketMessage> ForGuest(Ticket t) => t.Messages.Where(m => !m.Note);
}
