using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Support;

/// <summary>"Lost the link to your ticket?": [S] the same reply for every address, bot protection, at most 3 emails per address a day.</summary>
public sealed class LostLinkModel(SupportTickets tickets, BotGuard bots) : PageModel
{
    public const string Reply = "If there are tickets for that address, we've sent their links. Check your spam folder.";

    [BindProperty] public string? Email { get; set; }
    public bool Sent { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public IActionResult OnPost()
    {
        if (!bots.Check(HttpContext, "support").Passed)
        {
            Error = IndexModel.BotFailed;
            return Page();
        }
        tickets.SendLostLinks(Email);
        Sent = true;
        return Page();
    }
}
