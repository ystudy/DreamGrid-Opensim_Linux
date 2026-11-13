using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Tickets;
using GridGate.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages.Support;

/// <summary>
/// A guest's ticket (design 2026-11-05-support-tickets, "The ticket page"). [S] It opens by its private link (which sets the
/// <c>dg-ticket</c> cookie and comes back without the link in the address), by that cookie, or for the signed-in resident who sent
/// it. Anything else gets the same "this link has run out" page, whether or not the ticket exists.
/// </summary>
public sealed class TicketModel(SupportTickets tickets, TicketCookie cookie, IRobustClient robust, BotGuard bots,
    IStringLocalizer<ResidentText> loc) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Link { get; set; }
    [BindProperty] public string? Reply { get; set; }

    public Ticket? Ticket { get; private set; }
    public bool CanReply { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(Link))
        {
            if (tickets.Open(Id, Link) is { } opened)
            {
                cookie.Set(HttpContext, opened.Id);
                // Back without the link, so it isn't left in the address bar or the history.
                return LocalRedirect(SupportTickets.TicketPath + "?id=" + opened.Id);
            }
            return Page();
        }
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (Ticket is null) return Page();
        // [S] The same per-hour limit as the other public forms, so replies can't be sent by the thousand.
        if (!bots.CheckRate(HttpContext, "support-reply").Passed)
        {
            Error = loc["Support.TooManyReplies"];
            return Page();
        }
        if (tickets.Reply(Ticket.Id, Reply, HttpContext.Connection.RemoteIpAddress) is { } problem)
        {
            Error = problem.In(loc);
            return Page();
        }
        return LocalRedirect(SupportTickets.TicketPath + "?id=" + Ticket.Id + "&sent=1");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var t = tickets.ForGuest(Id);
        if (t is null) return;
        var byCookie = cookie.Read(HttpContext) == t.Id;
        var allowed = byCookie;
        if (!allowed && t.Sender.Account is { } owner)
            allowed = (await SupportGuest.AccountAsync(HttpContext, robust, ct))?.PrincipalId == owner;
        if (!allowed) return;
        if (byCookie) cookie.Set(HttpContext, t.Id); // 8 hours from the last use
        Ticket = t;
        CanReply = tickets.CanReply(t);
        if (Request.Query["sent"] == "1") Notice = loc["Support.ReplySent"];
    }
}
