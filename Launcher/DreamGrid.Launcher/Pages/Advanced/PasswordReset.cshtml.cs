using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>
/// The owner's password-recovery rules (OwnerPolicy; only where there is a guest side): one rule each for residents, staff and the
/// owner (Off / Email link / Email link + two-factor), the "staff must use two-factor" switch (the same setting the Security page
/// saves), and "Use a level" to apply one of Setup's answers again. Saves go through IGridController.UpdateSettings (no config
/// files change) and are audited as <c>recovery-settings</c>. A value that isn't one of the choices changes nothing.
/// </summary>
public sealed class PasswordResetModel(IGridController grid, Func<LauncherSettings> settings, EmailSender email, AuditLog audit) : PageModel
{
    [BindProperty] public string? Residents { get; set; }
    [BindProperty] public string? Staff { get; set; }
    [BindProperty] public string? Owner { get; set; }
    [BindProperty] public bool RequireTwoFactor { get; set; }
    [BindProperty] public string? Kind { get; set; }

    public RecoverySettings Rules { get; private set; } = new(RecoveryRule.Off, RecoveryRule.Off, RecoveryRule.Off);
    public bool Unset { get; private set; }
    public bool EmailReady { get; private set; }
    public string? Notice { get; private set; }
    public string? Error { get; private set; }

    public static string Title(RecoveryRule rule) => rule switch
    {
        RecoveryRule.EmailLink => "Email link",
        RecoveryRule.EmailLinkAndCode => "Email link + two-factor",
        _ => "Off",
    };

    public void OnGet() => Fill();

    public IActionResult OnPostSave()
    {
        if (!Enum.TryParse<RecoveryRule>(Residents, out var residents) || !Enum.IsDefined(residents)
            || !Enum.TryParse<RecoveryRule>(Staff, out var staff) || !Enum.IsDefined(staff)
            || !Enum.TryParse<RecoveryRule>(Owner, out var owner) || !Enum.IsDefined(owner))
        {
            Fill();
            Error = "Choose one of the options for each group.";
            return Page();
        }
        var rules = new RecoverySettings(residents, staff, owner);
        grid.UpdateSettings(s => s with { Recovery = rules, Security = s.Security with { RequireTwoFactorForStaff = RequireTwoFactor } },
            writeConfig: false);
        Write($"residents={residents}; staff={staff}; owner={owner}; require-two-factor={(RequireTwoFactor ? "on" : "off")}");
        Fill();
        Notice = "Saved. This applies now.";
        return Page();
    }

    public IActionResult OnPostLevel()
    {
        if (!Enum.TryParse<GridKind>(Kind, out var kind) || !Enum.IsDefined(kind))
        {
            Fill();
            Error = "Choose a level.";
            return Page();
        }
        grid.UpdateSettings(s => Recovery.Apply(s, kind), writeConfig: false);
        Write("level=" + kind);
        Fill();
        Notice = $"Saved: {Recovery.Title(kind)}. This applies now.";
        return Page();
    }

    private void Fill()
    {
        var s = settings();
        Unset = s.Recovery is null;
        Rules = Recovery.Rules(s);
        RequireTwoFactor = s.Security.RequireTwoFactorForStaff;
        EmailReady = email.IsReady;
    }

    private void Write(string target)
    {
        var (name, id, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(name, id, role, HttpContext.Connection.RemoteIpAddress?.ToString(), "recovery-settings", target, "ok"));
    }
}
