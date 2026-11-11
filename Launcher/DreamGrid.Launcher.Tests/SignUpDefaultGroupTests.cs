using System.Net;
using System.Security.Claims;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Groups;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// New residents join the default group when their sign-up becomes active, and at no other time (spec
/// 2026-11-02-groups-admin-design, "The default group"). Driven through the services the sign-up pages call.
/// </summary>
public sealed class SignUpDefaultGroupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly IPAddress Caller = IPAddress.Parse("192.168.1.61");

    /// <summary>A sign-up rig with a default group (founded by the owner).</summary>
    private static (SignUpsTests.Rig Rig, Guid Group) Rig(bool email = true, bool approval = false)
    {
        var rig = SignUpsTests.Factory(email: email);
        if (approval) rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s ? s with { Bots = s.Bots with { AdminApproval = true } } : null!);
        var group = rig.F.Groups.AddGroup("Everyone Here", TestAuth.OwnerId);
        Assert.True(rig.F.Services.GetRequiredService<GroupJoins>().SetDefault(group, []));
        return (rig, group);
    }

    private static Guid Bea(SignUpsTests.Rig rig) => rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId;

    [Fact]
    public async Task Open_sign_up_joins_the_default_group()
    {
        var (rig, group) = Rig(email: false);
        await using var _ = rig;
        var r = await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "en");
        Assert.Equal(SignUpOutcome.SignedUp, r.Outcome);
        Assert.True(rig.F.Groups.IsMember(group, Bea(rig)));
    }

    [Fact]
    public async Task Email_sign_up_joins_only_after_the_link()
    {
        var (rig, group) = Rig();
        await using var _ = rig;
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "en");
        Assert.False(rig.F.Groups.IsMember(group, Bea(rig)));

        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);
        var (result, _) = await rig.SignUps.ConfirmAsync(token, Caller, Ct);

        Assert.Equal(ConfirmResult.Confirmed, result);
        Assert.True(rig.F.Groups.IsMember(group, Bea(rig)));
    }

    [Fact]
    public async Task Staff_approved_sign_up_joins_on_approval()
    {
        var (rig, group) = Rig(email: false, approval: true);
        await using var _ = rig;
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "en");
        Assert.False(rig.F.Groups.IsMember(group, Bea(rig)));

        var approvals = rig.F.Services.GetRequiredService<SignUpApprovals>();
        Assert.Equal(ApprovalResult.Done, (await approvals.ApproveAsync(Bea(rig), "Test Owner", StaffRole.Owner, Caller, Ct)).Result);

        Assert.True(rig.F.Groups.IsMember(group, Bea(rig)));
    }

    [Fact]
    public async Task Denied_sign_up_never_joins()
    {
        var (rig, group) = Rig(email: false, approval: true);
        await using var _ = rig;
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "en");
        var bea = Bea(rig);
        var approvals = rig.F.Services.GetRequiredService<SignUpApprovals>();
        Assert.Equal(ApprovalResult.Done, (await approvals.DenyAsync(bea, "Test Owner", StaffRole.Owner, null, Caller, Ct)).Result);
        Assert.False(rig.F.Groups.IsMember(group, bea));
    }

    [Fact]
    public async Task Decoy_sign_up_never_joins()
    {
        var (rig, group) = Rig();
        await using var _ = rig;
        // ann@example.com is already on the grid (the rig's existing resident), so this sign-up is a decoy.
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), Caller, Ct, "en");
        Assert.False(rig.F.Groups.IsMember(group, Bea(rig)));
    }

    [Fact]
    public async Task Restored_suspended_account_is_not_joined()
    {
        var (rig, group) = Rig();
        await using var _ = rig;
        rig.F.Robust.Add("Sue", "Spended", -1, "p");
        var sue = rig.F.Robust.All.Single(a => a.FirstName == "Sue");
        var owner = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Test Owner"), new Claim(StaffSignIn.PrincipalClaim, TestAuth.OwnerId.ToString()),
             new Claim(ClaimTypes.Role, AuthSchemes.OwnerRole), new Claim(ClaimTypes.Role, AuthSchemes.AdminRole), new Claim(ClaimTypes.Role, AuthSchemes.StaffRole)], "test"));

        var outcome = await rig.F.Services.GetRequiredService<StaffActions>().ApplyAsync(owner, null, sue, StaffChange.Unsuspend, Ct);

        Assert.True(outcome.Done, outcome.Message);
        Assert.False(rig.F.Groups.IsMember(group, sue.PrincipalId));
    }

    [Fact]
    public async Task Sign_up_still_works_when_the_groups_database_is_down()
    {
        var (rig, group) = Rig(email: false);
        await using var _ = rig;
        rig.F.Groups.FailAfter(0);
        var r = await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "en");
        rig.F.Groups.FailAfter(null);
        Assert.Equal(SignUpOutcome.SignedUp, r.Outcome);
        Assert.False(rig.F.Groups.IsMember(group, Bea(rig)));
    }

    [Fact]
    public async Task Sign_up_with_the_default_deleted_in_the_viewer_joins_nothing()
    {
        var (rig, group) = Rig(email: false);
        await using var _ = rig;
        rig.F.Groups.Drop(group);
        var r = await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "en");
        Assert.Equal(SignUpOutcome.SignedUp, r.Outcome);
        Assert.Null(rig.F.Services.GetRequiredService<GroupJoins>().Default());
    }
}
