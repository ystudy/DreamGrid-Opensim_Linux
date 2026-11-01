using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Profile;

/// <summary>
/// The signed-in staff member's own switch for the "sign-ups waiting" email (StaffPolicy, by the /Profile folder convention; spec
/// 2026-11-01-sign-up-approval-design §8). Shown only to staff who may approve sign-ups; the others are told they can't.
/// </summary>
public sealed class EmailsModel(ApprovalNotices notices, Func<LauncherSettings> settings) : PageModel
{
    [BindProperty] public bool SignUpsWaiting { get; set; }

    [TempData] public string? Notice { get; set; }

    /// <summary>The signed-in staff member may approve sign-ups, so the switch applies to them.</summary>
    public bool CanApprove { get; private set; }

    public IActionResult OnGet()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        CanApprove = ApprovalRules.CanApprove(claims.Role, settings());
        SignUpsWaiting = !notices.OptedOut(claims.Principal);
        return Page();
    }

    public IActionResult OnPost()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        if (ApprovalRules.CanApprove(claims.Role, settings()))
        {
            notices.SetOptedOut(claims.Principal, !SignUpsWaiting);
            Notice = "Saved.";
        }
        return Redirect("/profile/emails");
    }
}
