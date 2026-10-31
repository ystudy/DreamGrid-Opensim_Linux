using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RecoveryTests
{
    [Fact]
    public void Null_means_all_off()
    {
        var s = TestSettings.Create();
        Assert.Null(s.Recovery);
        Assert.Equal(new RecoverySettings(RecoveryRule.Off, RecoveryRule.Off, RecoveryRule.Off), Recovery.Rules(s));
        Assert.False(Recovery.AnyOn(s));
    }

    [Fact]
    public void Levels_match_the_table()
    {
        Assert.Equal((new RecoverySettings(RecoveryRule.EmailLink, RecoveryRule.EmailLink, RecoveryRule.EmailLink), false),
            Recovery.Level(GridKind.HomeLab));
        Assert.Equal((new RecoverySettings(RecoveryRule.EmailLink, RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode), false),
            Recovery.Level(GridKind.SchoolOrClub));
        Assert.Equal((new RecoverySettings(RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode), true),
            Recovery.Level(GridKind.PublicGrid));
    }

    [Fact]
    public void RuleFor_picks_the_group()
    {
        var s = TestSettings.Create() with
        {
            Recovery = new RecoverySettings(RecoveryRule.EmailLink, RecoveryRule.EmailLinkAndCode, RecoveryRule.Off),
        };
        Assert.Equal(RecoveryRule.EmailLink, Recovery.RuleFor(s, StaffRole.None));
        Assert.Equal(RecoveryRule.EmailLinkAndCode, Recovery.RuleFor(s, StaffRole.Supervisor));
        Assert.Equal(RecoveryRule.EmailLinkAndCode, Recovery.RuleFor(s, StaffRole.Admin));
        Assert.Equal(RecoveryRule.Off, Recovery.RuleFor(s, StaffRole.Owner));
        Assert.True(Recovery.AnyOn(s));
    }

    [Fact]
    public void Apply_sets_rules_and_switch_and_keeps_other_security()
    {
        var s = TestSettings.Create() with { Security = new SecuritySettings(AdminLevel: 250, AdminNetworks: ["10.0.0.0/8"]) };

        var after = Recovery.Apply(s, GridKind.PublicGrid);

        Assert.Equal(Recovery.Level(GridKind.PublicGrid).Rules, after.Recovery);
        Assert.True(after.Security.RequireTwoFactorForStaff);
        Assert.Equal(250, after.Security.AdminLevel);
        Assert.Equal(["10.0.0.0/8"], after.Security.AdminNetworks!);

        var back = Recovery.Apply(after, GridKind.HomeLab);
        Assert.False(back.Security.RequireTwoFactorForStaff);
    }

    [Fact]
    public void Every_kind_has_a_title_and_a_line()
    {
        Assert.Equal("Home lab", Recovery.Title(GridKind.HomeLab));
        Assert.Equal("School or club", Recovery.Title(GridKind.SchoolOrClub));
        Assert.Equal("Public grid", Recovery.Title(GridKind.PublicGrid));
        foreach (var k in Enum.GetValues<GridKind>()) Assert.False(string.IsNullOrWhiteSpace(Recovery.Line(k)));
    }
}
