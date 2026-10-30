using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class StaffRoleTests
{
    private static readonly Guid OwnerId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    private static LauncherSettings Settings(OwnerPlan? plan) => TestSettings.Create() with
    {
        Plan = plan,
        Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true,
            Owner: new OwnerInfo("Estate", "Owner", OwnerId)),
    };

    private static RobustAccount Account(int level, Guid? id = null) => new(id ?? Guid.NewGuid(), "A", "B", level, "");

    [Theory]
    [InlineData(0, StaffRole.None)]
    [InlineData(99, StaffRole.None)]
    [InlineData(100, StaffRole.Supervisor)]
    [InlineData(199, StaffRole.Supervisor)]
    [InlineData(200, StaffRole.Admin)]
    [InlineData(250, StaffRole.Admin)]
    public void RoleOf_by_level_on_local(int level, StaffRole expected) =>
        Assert.Equal(expected, AdminRule.RoleOf(Settings(OwnerPlan.Local), Account(level)));

    [Fact]
    public void RoleOf_owner_is_owner_at_any_level() =>
        Assert.Equal(StaffRole.Owner, AdminRule.RoleOf(Settings(OwnerPlan.Local), Account(0, OwnerId)));

    [Fact]
    public void RoleOf_uses_level_settings()
    {
        var s = Settings(OwnerPlan.Local) with { Security = new SecuritySettings(SupervisorLevel: 50, AdminLevel: 150) };

        Assert.Equal(StaffRole.None, AdminRule.RoleOf(s, Account(49)));
        Assert.Equal(StaffRole.Supervisor, AdminRule.RoleOf(s, Account(50)));
        Assert.Equal(StaffRole.Supervisor, AdminRule.RoleOf(s, Account(149)));
        Assert.Equal(StaffRole.Admin, AdminRule.RoleOf(s, Account(150)));
    }

    [Fact]
    public void RoleOf_region_server_has_no_supervisors()
    {
        var s = Settings(OwnerPlan.RegionServer);

        Assert.Equal(StaffRole.None, AdminRule.RoleOf(s, Account(100)));
        Assert.Equal(StaffRole.Admin, AdminRule.RoleOf(s, Account(200)));
        Assert.Equal(StaffRole.Owner, AdminRule.RoleOf(s, Account(0, OwnerId)));
    }

    [Fact]
    public void RoleOf_connected_only_owner()
    {
        var s = Settings(OwnerPlan.Connected);

        Assert.Equal(StaffRole.None, AdminRule.RoleOf(s, Account(250)));
        Assert.Equal(StaffRole.Owner, AdminRule.RoleOf(s, Account(0, OwnerId)));
    }

    [Fact]
    public void RoleOf_no_owner_named_gives_nobody_owner() =>
        Assert.Equal(StaffRole.Admin, AdminRule.RoleOf(Settings(OwnerPlan.Local) with { Setup = new SetupState() }, Account(250)));

    [Fact]
    public void MayAdminister_follows_the_configured_admin_level()
    {
        var s = Settings(OwnerPlan.Local) with { Security = new SecuritySettings(AdminLevel: 150) };

        Assert.True(AdminRule.MayAdminister(s, Account(150)));
        Assert.False(AdminRule.MayAdminister(s, Account(149)));
        Assert.Equal(150, AdminRule.EffectiveLevel(s, Account(0, OwnerId)));
        Assert.Equal(149, AdminRule.EffectiveLevel(s, Account(149)));
    }

    [Theory]
    [InlineData(OwnerPlan.Local, true)]
    [InlineData(OwnerPlan.SimplePublic, true)]
    [InlineData(OwnerPlan.GridAsset, true)]
    [InlineData(OwnerPlan.RegionServer, false)]
    [InlineData(OwnerPlan.Connected, false)]
    [InlineData(null, true)]
    public void HasStaffPages(OwnerPlan? plan, bool expected) =>
        Assert.Equal(expected, AdminRule.HasStaffPages(Settings(plan)));
}
