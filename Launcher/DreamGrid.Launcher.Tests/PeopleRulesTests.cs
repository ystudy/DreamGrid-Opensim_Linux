using DreamGrid.Launcher.Core.Security;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class PeopleRulesTests
{
    private static PersonFacts Resident(bool suspended = false, bool hasEmail = true, bool emailReady = true) =>
        new(StaffRole.None, IsOwner: false, IsActor: false, Suspended: suspended, Waiting: false, HasEmail: hasEmail, EmailReady: emailReady);

    public static TheoryData<StaffRole, PersonFacts, PersonTask, string> Refusals => new()
    {
        { StaffRole.Admin, Resident() with { IsOwner = true, Judged = StaffRole.Owner }, PersonTask.Edit, "The grid owner can't be changed here." },
        { StaffRole.Owner, Resident() with { IsOwner = true, IsActor = true, Judged = StaffRole.Owner }, PersonTask.ResetLink, "The grid owner can't be changed here." },
        { StaffRole.Admin, Resident() with { IsActor = true, Judged = StaffRole.Admin }, PersonTask.SignOut, "Use your own account page." },
        { StaffRole.Admin, Resident() with { Waiting = true, Suspended = true }, PersonTask.Delete, "Approve or deny them on the Sign-ups page." },
        { StaffRole.Admin, Resident() with { Waiting = true, Suspended = true }, PersonTask.Unsuspend, "Approve or deny them on the Sign-ups page." },
        { StaffRole.Supervisor, Resident(), PersonTask.Edit, "Supervisors can only suspend or unsuspend." },
        { StaffRole.Supervisor, Resident(), PersonTask.ResetLink, "Supervisors can only suspend or unsuspend." },
        { StaffRole.Admin, Resident() with { Judged = StaffRole.Supervisor }, PersonTask.Edit, "Change staff on the Staff page first." },
        { StaffRole.Owner, Resident() with { Judged = StaffRole.Admin }, PersonTask.SignOut, "Change staff on the Staff page first." },
        { StaffRole.Admin, Resident(emailReady: false), PersonTask.ResetLink, "Email isn't set up." },
        { StaffRole.Admin, Resident(hasEmail: false), PersonTask.ResetLink, "No email address." },
        { StaffRole.Admin, Resident(suspended: true), PersonTask.ResetLink, "Suspended." },
        { StaffRole.Admin, Resident(), PersonTask.Delete, "Suspend them first." },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Refusal(StaffRole actor, PersonFacts target, PersonTask task, string expected) =>
        Assert.Equal(expected, PeopleRules.Refusal(actor, target, task));

    [Theory]
    [InlineData(StaffRole.Admin)]
    [InlineData(StaffRole.Owner)]
    public void Admin_can_do_everything_to_a_resident(StaffRole actor)
    {
        foreach (var task in new[] { PersonTask.Edit, PersonTask.ResetLink, PersonTask.SignOut, PersonTask.Suspend })
            Assert.Null(PeopleRules.Refusal(actor, Resident(), task));
        foreach (var task in new[] { PersonTask.Edit, PersonTask.SignOut, PersonTask.Unsuspend, PersonTask.Delete })
            Assert.Null(PeopleRules.Refusal(actor, Resident(suspended: true), task));
    }

    [Fact]
    public void Suspended_admin_is_judged_as_admin()
    {
        var admin = Resident(suspended: true) with { Judged = StaffRole.Admin };
        Assert.Equal("Change staff on the Staff page first.", PeopleRules.Refusal(StaffRole.Admin, admin, PersonTask.Edit));
        Assert.Equal("Change staff on the Staff page first.", PeopleRules.Refusal(StaffRole.Admin, admin, PersonTask.Delete));
        Assert.Equal("Change staff on the Staff page first.", PeopleRules.Refusal(StaffRole.Admin, admin, PersonTask.ResetLink));
        Assert.Equal("Admins can only suspend or unsuspend supervisors and guests.", PeopleRules.Refusal(StaffRole.Admin, admin, PersonTask.Unsuspend));
    }

    [Fact]
    public void Supervisor_can_suspend_a_resident_and_nothing_else()
    {
        Assert.Null(PeopleRules.Refusal(StaffRole.Supervisor, Resident(), PersonTask.Suspend));
        Assert.Null(PeopleRules.Refusal(StaffRole.Supervisor, Resident(suspended: true), PersonTask.Unsuspend));
        foreach (var task in new[] { PersonTask.Edit, PersonTask.ResetLink, PersonTask.SignOut, PersonTask.Delete })
            Assert.Equal("Supervisors can only suspend or unsuspend.", PeopleRules.Refusal(StaffRole.Supervisor, Resident(suspended: true), task));
    }

    [Fact]
    public void Unsuspend_follows_StaffChanges()
    {
        var supervisor = Resident(suspended: true) with { Judged = StaffRole.Supervisor };
        Assert.Equal("Supervisors can only suspend or unsuspend guests.", PeopleRules.Refusal(StaffRole.Supervisor, supervisor, PersonTask.Unsuspend));
        Assert.Null(PeopleRules.Refusal(StaffRole.Admin, supervisor, PersonTask.Unsuspend));
        Assert.Equal("You can't suspend yourself.", PeopleRules.Refusal(StaffRole.Admin, Resident() with { IsActor = true, Judged = StaffRole.Admin }, PersonTask.Suspend));
    }
}
