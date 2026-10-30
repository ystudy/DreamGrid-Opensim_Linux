using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Profile;

/// <summary>
/// The signed-in staff member's own sessions (StaffPolicy, by the /Profile folder convention): when each started and was last
/// seen, from where, in which browser. One can be ended, or all of them ("Sign out everywhere", audited), which also signs
/// this browser out.
/// </summary>
public sealed class SessionsModel(StaffSessions sessions, AuditLog audit) : PageModel
{
    public IReadOnlyList<StaffSession> Sessions { get; private set; } = [];

    /// <summary>This browser's session id.</summary>
    public string? Current { get; private set; }

    public IActionResult OnGet()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        Sessions = sessions.List(claims.Principal);
        Current = claims.SessionId;
        return Page();
    }

    public async Task<IActionResult> OnPostEndAsync(string? id)
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        if (string.IsNullOrEmpty(id)) return Redirect("/profile/sessions");
        // Only the person's own sessions can be named: the list is per principal.
        sessions.End(claims.Principal, id);
        if (id != claims.SessionId) return Redirect("/profile/sessions");
        await HttpContext.SignOutAsync(AuthSchemes.Admin);
        return Redirect("/login");
    }

    public async Task<IActionResult> OnPostEndAllAsync()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        sessions.EndAll(claims.Principal);
        audit.Write(new AuditEntry(LogText.Clean(User.Identity?.Name, 64), claims.Principal, claims.Role.AuditName(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), "sign-out-everywhere", null, "ok"));
        await HttpContext.SignOutAsync(AuthSchemes.Admin);
        return Redirect("/login");
    }
}
