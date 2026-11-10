using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Tickets;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher.Pages.Support;

/// <summary>
/// Sending a support ticket (design 2026-11-05-support-tickets, "Sending a ticket"). A signed-in resident sends at once and sees
/// their own tickets here; someone not signed in gives a name and email and confirms it. Bot protection: form <c>support</c>.
/// </summary>
public sealed class IndexModel(SupportTickets tickets, BotGuard bots, IRobustClient robust, TicketCookie cookie,
    IStringLocalizer<ResidentText> t) : PageModel
{
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Category { get; set; }
    [BindProperty] public string? Subject { get; set; }
    [BindProperty] public string? Message { get; set; }
    [BindProperty] public string? Evidence { get; set; }
    [BindProperty] public string? Logs { get; set; }

    public RobustAccount? Account { get; private set; }
    public IReadOnlyList<Ticket> Mine { get; private set; } = [];
    public bool CheckEmail { get; private set; }
    public bool AnonymousOpen => tickets.AnonymousOpen;
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        var check = Account is null ? bots.Check(HttpContext, "support") : bots.CheckRate(HttpContext, "support");
        if (!check.Passed)
        {
            Error = t["Common.BotFailed"];
            return Page();
        }
        var r = tickets.Send(new TicketForm(Name, Email, Category, Subject, Message, Evidence, Logs), Account, HttpContext.Connection.RemoteIpAddress);
        switch (r.Outcome)
        {
            case TicketSendOutcome.Opened:
                cookie.Set(HttpContext, r.Ticket!.Id);
                return LocalRedirect(SupportTickets.TicketPath + "?id=" + r.Ticket.Id);
            case TicketSendOutcome.CheckEmail:
                CheckEmail = true;
                return Page();
            default:
                Error = (r.Message ?? new ResidentMessage("Support.Busy")).In(t);
                return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Account = await SupportGuest.AccountAsync(HttpContext, robust, ct);
        if (Account is not null) Mine = tickets.OfAccount(Account.PrincipalId);
    }
}
