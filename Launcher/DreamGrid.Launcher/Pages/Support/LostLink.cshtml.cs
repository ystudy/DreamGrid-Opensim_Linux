using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher.Pages.Support;

/// <summary>"Lost the link to your ticket?": [S] the same reply for every address, bot protection, at most 3 emails per address a day.</summary>
public sealed class LostLinkModel(SupportTickets tickets, BotGuard bots, EmailLanguage emailLanguage, IStringLocalizer<ResidentText> t) : PageModel
{
    [BindProperty] public string? Email { get; set; }
    public bool Sent { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public IActionResult OnPost()
    {
        if (!bots.Check(HttpContext, "support").Passed)
        {
            Error = t["Common.BotFailed"];
            return Page();
        }
        tickets.SendLostLinks(Email, emailLanguage.ForRequest(HttpContext));
        Sent = true;
        return Page();
    }
}
