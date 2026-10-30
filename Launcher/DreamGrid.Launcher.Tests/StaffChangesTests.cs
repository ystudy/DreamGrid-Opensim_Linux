using DreamGrid.Launcher.Core.Security;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Who may make which change to whom (the Staff and People pages ask this before touching a level).</summary>
public sealed class StaffChangesTests
{
    private static readonly StaffRole[] Actors = [StaffRole.None, StaffRole.Supervisor, StaffRole.Admin, StaffRole.Owner];
    private static readonly StaffRole[] Targets = [StaffRole.None, StaffRole.Supervisor, StaffRole.Admin];
    private static readonly StaffChange[] Changes = Enum.GetValues<StaffChange>();

    /// <summary>Every allowed (actor, target, change), written out; any other combination must be refused.</summary>
    private static readonly HashSet<(StaffRole, StaffRole, StaffChange)> Allowed =
    [
        // The owner may do anything to anyone but the owner.
        .. from target in Targets from change in Changes select (StaffRole.Owner, target, change),
        // An admin may suspend and unsuspend supervisors and guests.
        (StaffRole.Admin, StaffRole.Supervisor, StaffChange.Suspend),
        (StaffRole.Admin, StaffRole.Supervisor, StaffChange.Unsuspend),
        (StaffRole.Admin, StaffRole.None, StaffChange.Suspend),
        (StaffRole.Admin, StaffRole.None, StaffChange.Unsuspend),
        // A supervisor may suspend and unsuspend guests.
        (StaffRole.Supervisor, StaffRole.None, StaffChange.Suspend),
        (StaffRole.Supervisor, StaffRole.None, StaffChange.Unsuspend),
    ];

    public static TheoryData<StaffRole, StaffRole, StaffChange> Combinations()
    {
        var data = new TheoryData<StaffRole, StaffRole, StaffChange>();
        foreach (var actor in Actors)
            foreach (var target in Targets)
                foreach (var change in Changes)
                    data.Add(actor, target, change);
        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Every_combination_is_allowed_or_refused_as_the_rules_say(StaffRole actor, StaffRole target, StaffChange change)
    {
        var refusal = StaffChanges.Refusal(actor, target, targetIsOwner: false, change);

        if (Allowed.Contains((actor, target, change)))
            Assert.Null(refusal);
        else
            Assert.False(string.IsNullOrWhiteSpace(refusal), $"{actor} should not {change} a {target}");
    }

    [Theory]
    [InlineData(StaffRole.None)]
    [InlineData(StaffRole.Supervisor)]
    [InlineData(StaffRole.Admin)]
    [InlineData(StaffRole.Owner)]
    public void Nobody_changes_owner(StaffRole actor)
    {
        foreach (var change in Changes)
            Assert.Equal("The grid owner can't be changed here.", StaffChanges.Refusal(actor, StaffRole.Owner, targetIsOwner: true, change));
    }

    [Theory]
    [InlineData(StaffRole.Supervisor, StaffRole.None)]
    [InlineData(StaffRole.Admin, StaffRole.None)]
    [InlineData(StaffRole.Admin, StaffRole.Supervisor)]
    [InlineData(StaffRole.Owner, StaffRole.Admin)]
    public void Nobody_suspends_themselves(StaffRole actor, StaffRole target)
    {
        Assert.Equal("You can't suspend yourself.", StaffChanges.Refusal(actor, target, targetIsOwner: false, StaffChange.Suspend, targetIsActor: true));
        // Unsuspending oneself is impossible anyway (a suspended person can't sign in), so only the role rules apply.
        Assert.Equal(Allowed.Contains((actor, target, StaffChange.Unsuspend)),
            StaffChanges.Refusal(actor, target, targetIsOwner: false, StaffChange.Unsuspend, targetIsActor: true) is null);
    }

    [Fact]
    public void Refusals_say_why_in_plain_words()
    {
        Assert.Equal("Only the grid owner can change who is staff.",
            StaffChanges.Refusal(StaffRole.Admin, StaffRole.None, false, StaffChange.MakeSupervisor));
        Assert.Equal("Supervisors can only suspend or unsuspend guests.",
            StaffChanges.Refusal(StaffRole.Supervisor, StaffRole.Admin, false, StaffChange.Suspend));
        Assert.Equal("Admins can only suspend or unsuspend supervisors and guests.",
            StaffChanges.Refusal(StaffRole.Admin, StaffRole.Admin, false, StaffChange.Suspend));
    }
}
