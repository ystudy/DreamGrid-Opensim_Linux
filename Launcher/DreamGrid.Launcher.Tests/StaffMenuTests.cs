using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Navigation;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>What the staff menu shows each role on each plan (design 2026-11-05-gridgate-menu-looks-workflows §1).</summary>
public sealed class StaffMenuTests
{
    private static LauncherSettings On(OwnerPlan plan) => TestSettings.Create() with { Plan = plan };

    private static string[] Top(StaffRole role, OwnerPlan plan, bool canApprove = false) =>
        StaffMenu.For(role, On(plan), canApprove).Select(g => g.IsSingle ? g.SingleText : g.Text).ToArray();

    private static string[] Inside(StaffRole role, OwnerPlan plan, string group, bool canApprove = false) =>
        StaffMenu.For(role, On(plan), canApprove).Single(g => g.Text == group).Items.Select(i => i.Text).ToArray();

    [Theory]
    [InlineData(OwnerPlan.Local)]
    [InlineData(OwnerPlan.SimplePublic)]
    [InlineData(OwnerPlan.GridAsset)]
    public void Owner_on_a_grid_plan(OwnerPlan plan)
    {
        Assert.Equal(["Home", "World", "People", "Guest pages", "Security", "Settings", "You"], Top(StaffRole.Owner, plan, canApprove: true));
        Assert.Equal(["Regions", "Estates", "Hypergrid links", "OAR library", "Recently deleted"], Inside(StaffRole.Owner, plan, "World"));
        Assert.Equal(["Find a person", "Sign-ups", "Tickets", "Groups", "Takedowns", "Staff"], Inside(StaffRole.Owner, plan, "People", canApprove: true));
        Assert.Equal(["Splash page", "Legal pages", "Page wording", "Sign-up and bots", "Email server", "Password reset"], Inside(StaffRole.Owner, plan, "Guest pages"));
        Assert.Equal(["Plan", "Network", "Database", "Region defaults", "Logs"], Inside(StaffRole.Owner, plan, "Settings"));
        Assert.Equal(["Sessions", "Two-factor", "Email notices"], Inside(StaffRole.Owner, plan, "You"));
    }

    [Fact]
    public void Admin_on_a_grid_plan()
    {
        Assert.Equal(["Home", "World", "People", "Splash page", "Settings", "You"], Top(StaffRole.Admin, OwnerPlan.Local, canApprove: true));
        Assert.Equal(["Find a person", "Sign-ups", "Tickets", "Groups", "Takedowns"], Inside(StaffRole.Admin, OwnerPlan.Local, "People", canApprove: true));
        Assert.Equal(["Find a person", "Tickets", "Groups", "Takedowns"], Inside(StaffRole.Admin, OwnerPlan.Local, "People"));
    }

    [Fact]
    public void Supervisor_who_cannot_approve_sees_Hypergrid_People_and_their_own_menu()
    {
        Assert.Equal(["Hypergrid links", "People", "You"], Top(StaffRole.Supervisor, OwnerPlan.Local));
        Assert.Equal(["Find a person", "Tickets"], Inside(StaffRole.Supervisor, OwnerPlan.Local, "People"));
        var world = StaffMenu.For(StaffRole.Supervisor, On(OwnerPlan.Local), false).Single(g => g.Text == "World");
        Assert.Equal("/hypergrid", world.SingleHref);
    }

    [Fact]
    public void Supervisor_who_may_approve_gets_a_People_dropdown()
    {
        Assert.Equal(["Find a person", "Sign-ups", "Tickets"], Inside(StaffRole.Supervisor, OwnerPlan.Local, "People", canApprove: true));
        var signUps = StaffMenu.For(StaffRole.Supervisor, On(OwnerPlan.Local), true).Single(g => g.Text == "People").Items[1];
        Assert.True(signUps.Badge);
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public void Owner_without_a_guest_side(OwnerPlan plan)
    {
        Assert.Equal(["Home", "World", "Security", "Settings", "You"], Top(StaffRole.Owner, plan, canApprove: true));
        Assert.Equal(["Regions", "OAR library", "Recently deleted"], Inside(StaffRole.Owner, plan, "World"));
        Assert.Equal(["Sessions", "Two-factor"], Inside(StaffRole.Owner, plan, "You"));
        Assert.False(StaffMenu.For(StaffRole.Owner, On(plan), true)[0].Items[0].Badge);
    }

    [Fact]
    public void Admin_on_a_region_server()
    {
        Assert.Equal(["Home", "World", "Settings", "You"], Top(StaffRole.Admin, OwnerPlan.RegionServer));
    }

    [Fact]
    public void Only_one_badge_shows()
    {
        foreach (var role in new[] { StaffRole.Owner, StaffRole.Admin, StaffRole.Supervisor })
            Assert.Single(StaffMenu.For(role, On(OwnerPlan.Local), true).SelectMany(g => g.Items), i => i.Badge);
    }

    [Fact]
    public void A_single_link_is_not_marked_on_a_hub_it_does_not_link_to()
    {
        var groups = StaffMenu.For(StaffRole.Admin, On(OwnerPlan.Local), true);
        Assert.Null(StaffMenu.Active(groups, "/guest-pages")); // the admin's Guest pages group is the Splash page link
        Assert.Equal("Guest pages", StaffMenu.Active(groups, "/splash")!.Text);
    }

    [Theory]
    [InlineData("/", "Home")]
    [InlineData("/regions/region/abc", "World")]
    [InlineData("/estates/estate", "World")]
    [InlineData("/people/1234", "People")]
    [InlineData("/security/legal", "Guest pages")]
    [InlineData("/security/email/wording", "Guest pages")]
    [InlineData("/advanced/password-reset", "Guest pages")]
    [InlineData("/security", "Security")]
    [InlineData("/advanced/plan", "Settings")]
    [InlineData("/advanced/module/x", "Settings")]
    [InlineData("/logs", "Settings")]
    [InlineData("/profile/emails", "You")]
    public void The_page_marks_its_group_active(string path, string group)
    {
        var groups = StaffMenu.For(StaffRole.Owner, On(OwnerPlan.Local), true);
        Assert.Equal(group, StaffMenu.Active(groups, path)!.Text);
    }
}
