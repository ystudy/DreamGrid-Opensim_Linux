using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>The owner's two approval switches (spec 2026-11-01-sign-up-approval-design §6).</summary>
public static class ApprovalRules
{
    /// <summary>Whether someone in <paramref name="role"/> may approve or deny a sign-up. The owner always may; nobody outside staff ever may.</summary>
    public static bool CanApprove(StaffRole role, LauncherSettings s) => role switch
    {
        StaffRole.Owner => true,
        StaffRole.Admin => s.Bots.Approvers != SignUpApprovers.Owner,
        StaffRole.Supervisor => s.Bots.Approvers == SignUpApprovers.OwnerAdminsAndSupervisors,
        _ => false,
    };

    /// <summary>A new sign-up waits for staff: the grid can't send its confirm link and the owner asked for approval.</summary>
    public static bool WaitsForStaff(LauncherSettings s, bool emailReady) => !emailReady && s.Bots.AdminApproval;
}
