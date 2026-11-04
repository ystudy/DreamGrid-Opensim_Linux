using System.Security.Claims;
using System.Text.Json;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Deleting a suspended resident from the People page (spec 2026-11-03-people-admin-design, Delete).</summary>
public sealed class PeopleDeleteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid SueId = Guid.Parse("0d000000-0000-0000-0000-00000000000d");
    private static readonly Guid RegionId = Guid.Parse("0c000000-0000-0000-0000-00000000000c");

    private sealed class HoldAll : AccountHolds
    {
        public override bool IsHeld(Guid principal) => true;
    }

    private static LauncherFactory Factory(bool hold = false)
    {
        var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            ExtraServices = services =>
            {
                if (!hold) return;
                services.RemoveAll<AccountHolds>();
                services.AddSingleton<AccountHolds>(new HoldAll());
            },
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Sue", "Spended", -1, "pass-word1", SueId, email: "sue@example.com");
        f.Robust.Add("Gail", "Guest", 0, "pass-word1", email: "gail@example.com");
        return f;
    }

    private static ClaimsPrincipal Admin()
    {
        var a = TestAuth.Account(StaffRole.Admin);
        List<Claim> claims =
        [
            new(ClaimTypes.Name, $"{a.FirstName} {a.LastName}"),
            new(StaffSignIn.PrincipalClaim, a.PrincipalId.ToString()),
            .. StaffSignIn.RoleClaims(StaffRole.Admin).Select(r => new Claim(ClaimTypes.Role, r)),
            new(StaffSignIn.SessionClaim, "test"),
            new(StaffSignIn.StampClaim, "test"),
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Admin));
    }

    private static PeopleActions Actions(LauncherFactory f) => f.Services.GetRequiredService<PeopleActions>();

    private static List<JsonElement> Audit(LauncherFactory f, string action)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("action").GetString() == action).ToList();
    }

    [Fact]
    public async Task Not_suspended_is_refused()
    {
        await using var f = Factory();
        var gail = f.Robust.All.Single(a => a.FirstName == "Gail").PrincipalId;
        var r = await Actions(f).DeleteAsync(Admin(), null, gail, Ct);
        Assert.Equal("Suspend them first.", r.Message);
        Assert.Empty(f.Remover.Everything);
    }

    [Fact]
    public async Task Held_account_is_refused()
    {
        await using var f = Factory(hold: true);
        var r = await Actions(f).DeleteAsync(Admin(), null, SueId, Ct);
        Assert.Equal(PeopleActions.OnHold, r.Message);
        Assert.Empty(f.Remover.Everything);
        Assert.Equal(PeopleActions.OnHold, (await Actions(f).PreviewDeleteAsync(Admin(), SueId, Ct))!.Refusal);
    }

    [Fact]
    public async Task Regions_go_back_first_and_delete_waits()
    {
        await using var f = Factory();
        var rights = f.Services.GetRequiredService<RegionRights>();
        rights.Grant(RegionId, SueId, "Sue Spended");
        rights.MoveDone(RegionId);
        rights.SetHomeEstate(RegionId, 1);

        var r = await Actions(f).DeleteAsync(Admin(), null, SueId, Ct);

        Assert.Equal(PeopleActions.Moving, r.Message);
        Assert.Equal(EstateMove.ToGrid, rights.Of(RegionId)!.Pending);
        Assert.Empty(f.Remover.Everything);
    }

    [Fact]
    public async Task Delete_after_moves_removes_everything_and_audits()
    {
        await using var f = Factory();
        var rights = f.Services.GetRequiredService<RegionRights>();
        rights.SetEstate(SueId, 7, "Sue Spended Estate");
        f.Services.GetRequiredService<HeldNames>().Hold("Sue Old", SueId);

        var r = await Actions(f).DeleteAsync(Admin(), null, SueId, Ct);

        Assert.True(r.Done, r.Message);
        Assert.Equal("Sue Spended is deleted.", r.Message);
        Assert.Equal([SueId], f.Remover.Everything);
        Assert.Equal([7], f.Estates.Removed);
        Assert.Null(rights.EstateOf(SueId));
        Assert.False(f.Services.GetRequiredService<HeldNames>().IsHeld("Sue Old"));
        Assert.DoesNotContain(f.Robust.All, a => a.PrincipalId == SueId);
        Assert.Equal("ok", Assert.Single(Audit(f, "deleted")).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Estate_failure_after_delete_says_so()
    {
        await using var f = Factory();
        f.Services.GetRequiredService<RegionRights>().SetEstate(SueId, 7, "Sue Spended Estate");
        f.Estates.Down = true;

        var r = await Actions(f).DeleteAsync(Admin(), null, SueId, Ct);

        Assert.Equal("Sue Spended was deleted, but their empty estate couldn't be removed. Check the log.", r.Message);
        Assert.Equal("failed", Assert.Single(Audit(f, "deleted")).GetProperty("result").GetString());
        Assert.Equal([SueId], f.Remover.Everything);
    }

    [Fact]
    public async Task Second_delete_of_a_gone_account_is_a_message_not_an_error()
    {
        await using var f = Factory();
        Assert.True((await Actions(f).DeleteAsync(Admin(), null, SueId, Ct)).Done);
        Assert.Equal(PeopleActions.Gone, (await Actions(f).DeleteAsync(Admin(), null, SueId, Ct)).Message);
    }

    [Fact]
    public async Task Preview_lists_the_regions_that_move()
    {
        await using var f = Factory();
        f.Services.GetRequiredService<RegionRights>().Grant(RegionId, SueId, "Sue Spended");
        var preview = await Actions(f).PreviewDeleteAsync(Admin(), SueId, Ct);
        Assert.NotNull(preview);
        Assert.Null(preview.Refusal);
        Assert.Single(preview.RegionsMoving);
    }

    [Fact]
    public void Everything_list_covers_the_tables_that_name_an_avatar()
    {
        var all = string.Join("\n", AccountRemover.EverythingStatements.Select(s => s.Sql));
        foreach (var table in new[] { "inventoryitems", "inventoryfolders", "Friends", "os_groups_membership", "os_groups_rolemembership",
                     "os_groups_invites", "os_groups_principals", "userprofile", "userpicks", "classifieds", "usernotes", "usersettings",
                     "userdata", "im_offline", "MuteList", "AgentPrefs", "hg_traveling_data", "Avatars", "GridUser", "Presence", "tokens",
                     "auth", "UserAccounts" })
            Assert.Contains($"`robust`.`{table}`", all);
        Assert.EndsWith("`UserAccounts` WHERE `PrincipalID` = @id", AccountRemover.EverythingStatements[^1].Sql);
    }
}
