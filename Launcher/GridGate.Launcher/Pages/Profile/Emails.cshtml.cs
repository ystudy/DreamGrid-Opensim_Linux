using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Tickets;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Profile;

/// <summary>
/// The signed-in staff member's own email switches (StaffPolicy, by the /Profile folder convention): "sign-ups waiting" (spec
/// 2026-11-01-sign-up-approval-design §8), shown only to staff who may approve sign-ups, and "New support tickets" (design
/// 2026-11-05-support-tickets, "Notices"), off by default and shown to every staff member on a grid with staff pages.
/// </summary>
public sealed class EmailsModel(ApprovalNotices notices, TicketNotices ticketNotices, Func<LauncherSettings> settings, TimeProvider time) : PageModel
{
    [BindProperty] public bool SignUpsWaiting { get; set; }
    [BindProperty] public bool NewTickets { get; set; }

    [TempData] public string? Notice { get; set; }

    /// <summary>The signed-in staff member may approve sign-ups, so that switch applies to them.</summary>
    public bool CanApprove { get; private set; }

    /// <summary>This grid has support tickets for staff (not a region server or a connected grid).</summary>
    public bool HasTickets { get; private set; }

    public IActionResult OnGet()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        var s = settings();
        CanApprove = ApprovalRules.CanApprove(claims.Role, s);
        HasTickets = AdminRule.HasStaffPages(s);
        SignUpsWaiting = !notices.OptedOut(claims.Principal);
        NewTickets = ticketNotices.IsOn(claims.Principal);
        return Page();
    }

    public IActionResult OnPost()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        var s = settings();
        var saved = false;
        if (ApprovalRules.CanApprove(claims.Role, s))
        {
            notices.SetOptedOut(claims.Principal, !SignUpsWaiting);
            saved = true;
        }
        if (AdminRule.HasStaffPages(s))
        {
            ticketNotices.Set(claims.Principal, NewTickets, time.GetUtcNow());
            saved = true;
        }
        if (saved) Notice = "Saved.";
        return Redirect("/profile/emails");
    }
}
