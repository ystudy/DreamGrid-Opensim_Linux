using GridGate.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Support;

/// <summary>
/// The confirmation link: a ticket from someone not signed in goes to staff and opens for them. Opening the link shows a button
/// rather than confirming at once, so a mail scanner that follows links can't send the ticket.
/// </summary>
public sealed class ConfirmModel(SupportTickets tickets, TicketCookie cookie) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }

    public bool Failed { get; private set; }

    public void OnGet() { }

    public IActionResult OnPost()
    {
        if (tickets.Confirm(Token) is not { } t)
        {
            Failed = true;
            return Page();
        }
        cookie.Set(HttpContext, t.Id);
        return LocalRedirect(SupportTickets.TicketPath + "?id=" + t.Id);
    }
}
