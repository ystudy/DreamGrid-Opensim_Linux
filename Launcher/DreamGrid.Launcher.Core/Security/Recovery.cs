using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>The answer to Setup's "What kind of grid is this?", which fills in the three recovery rules.</summary>
public enum GridKind { HomeLab, SchoolOrClub, PublicGrid }

/// <summary>
/// The owner's password-recovery rules: one each for residents, staff (supervisors and admins) and the owner. No rules chosen
/// (every install from before password recovery) reads as all <see cref="RecoveryRule.Off"/>, so nothing sends email until the
/// owner picks a level.
/// </summary>
public static class Recovery
{
    private static readonly RecoverySettings AllOff = new(RecoveryRule.Off, RecoveryRule.Off, RecoveryRule.Off);

    public static RecoverySettings Rules(LauncherSettings s) => s.Recovery ?? AllOff;

    /// <summary>The rule for an account of <paramref name="role"/>: the rule follows the account, never the page it asked on.</summary>
    public static RecoveryRule RuleFor(LauncherSettings s, StaffRole role)
    {
        var r = Rules(s);
        return role switch
        {
            StaffRole.Owner => r.Owner,
            StaffRole.Supervisor or StaffRole.Admin => r.Staff,
            _ => r.Residents,
        };
    }

    /// <summary>True when at least one group may reset its password.</summary>
    public static bool AnyOn(LauncherSettings s)
    {
        var r = Rules(s);
        return r.Residents != RecoveryRule.Off || r.Staff != RecoveryRule.Off || r.Owner != RecoveryRule.Off;
    }

    /// <summary>The rules and the "staff must use two-factor" switch for each Setup answer (spec §4, agreed with the owner).</summary>
    public static (RecoverySettings Rules, bool StaffMustUseTwoFactor) Level(GridKind kind) => kind switch
    {
        GridKind.SchoolOrClub => (new(RecoveryRule.EmailLink, RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode), false),
        GridKind.PublicGrid => (new(RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode), true),
        _ => (new(RecoveryRule.EmailLink, RecoveryRule.EmailLink, RecoveryRule.EmailLink), false),
    };

    /// <summary><paramref name="s"/> with the rules and the staff two-factor switch of <paramref name="kind"/>; other security settings kept.</summary>
    public static LauncherSettings Apply(LauncherSettings s, GridKind kind)
    {
        var (rules, staffTwoFactor) = Level(kind);
        return s with { Recovery = rules, Security = s.Security with { RequireTwoFactorForStaff = staffTwoFactor } };
    }

    public static string Title(GridKind kind) => kind switch
    {
        GridKind.SchoolOrClub => "School or club",
        GridKind.PublicGrid => "Public grid",
        _ => "Home lab",
    };

    public static string Line(GridKind kind) => kind switch
    {
        GridKind.SchoolOrClub => "A group that knows each other. Staff need their two-factor code to reset a password.",
        GridKind.PublicGrid => "Open to the public. Everyone who sets up two-factor needs their code to reset a password, and staff must use two-factor to sign in.",
        _ => "Just you, family or friends. An email link is enough to reset a password.",
    };
}
