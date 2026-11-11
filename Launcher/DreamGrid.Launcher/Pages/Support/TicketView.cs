using DreamGrid.Launcher.Core.Tickets;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher.Pages.Support;

/// <summary>How the guest pages word a ticket.</summary>
public static class TicketView
{
    public static string StateText(TicketState s, IStringLocalizer<ResidentText> t) => s switch
    {
        TicketState.Open => t["Support.StateOpen"].Value,
        TicketState.Waiting => t["Support.StateWaiting"].Value,
        TicketState.Closed => t["Support.StateClosed"].Value,
        _ => t["Support.StateUnsent"].Value,
    };

    /// <summary>The category's name in the page's language (staff pages use <see cref="TicketCategories.Title"/>).</summary>
    public static string CategoryText(string id, IStringLocalizer<ResidentText> t) => id switch
    {
        TicketCategories.Accounts => t["Support.CategoryAccounts"].Value,
        TicketCategories.Land => t["Support.CategoryLand"].Value,
        TicketCategories.Abuse => t["Support.CategoryAbuse"].Value,
        TicketCategories.Technical => t["Support.CategoryTechnical"].Value,
        _ => t["Support.CategoryOther"].Value,
    };

    /// <summary>The messages a guest may see: never a staff-only note.</summary>
    public static IEnumerable<TicketMessage> ForGuest(Ticket t) => t.Messages.Where(m => !m.Note);
}
