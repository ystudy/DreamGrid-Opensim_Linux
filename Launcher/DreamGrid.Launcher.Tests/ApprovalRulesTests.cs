using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Who may approve sign-ups, and when a sign-up waits for staff (spec 2026-11-01-sign-up-approval-design §6).</summary>
public sealed class ApprovalRulesTests
{
    private static LauncherSettings With(SignUpApprovers who, bool approval = false) =>
        TestSettings.Create() with { Bots = new BotSettings(AdminApproval: approval, Approvers: who) };

    [Theory]
    [InlineData(SignUpApprovers.Owner, true, false, false)]
    [InlineData(SignUpApprovers.OwnerAndAdmins, true, true, false)]
    [InlineData(SignUpApprovers.OwnerAdminsAndSupervisors, true, true, true)]
    public void CanApprove_matrix(SignUpApprovers who, bool owner, bool admin, bool supervisor)
    {
        var s = With(who);
        Assert.Equal(owner, ApprovalRules.CanApprove(StaffRole.Owner, s));
        Assert.Equal(admin, ApprovalRules.CanApprove(StaffRole.Admin, s));
        Assert.Equal(supervisor, ApprovalRules.CanApprove(StaffRole.Supervisor, s));
        Assert.False(ApprovalRules.CanApprove(StaffRole.None, s));
    }

    [Fact]
    public void Default_is_owner_and_admins()
    {
        Assert.Equal(SignUpApprovers.OwnerAndAdmins, new BotSettings().Approvers);
        Assert.False(new BotSettings().AdminApproval);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void WaitsForStaff_only_without_email_and_switch_on(bool emailReady, bool approval, bool waits)
    {
        Assert.Equal(waits, ApprovalRules.WaitsForStaff(With(SignUpApprovers.OwnerAndAdmins, approval), emailReady));
    }
}
