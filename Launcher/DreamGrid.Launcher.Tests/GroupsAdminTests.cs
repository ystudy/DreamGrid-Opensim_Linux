using System.Security.Claims;
using DreamGrid.Launcher.Core.Groups;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Groups;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The admin's group actions, the default group and their audit lines (spec 2026-11-02-groups-admin-design).</summary>
public sealed class GroupsAdminTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "groups-admin-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRobustAccounts _robust = new();
    private readonly FakeGridGroups _groups;
    private readonly AuditLog _audit;
    private readonly ClaimsPrincipal _admin = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "Ada Admin"), new Claim(StaffSignIn.PrincipalClaim, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, AuthSchemes.AdminRole)],
        "test"));
    private readonly Guid _founder = Guid.NewGuid();
    private GroupJoins _joins;
    private GroupsAdmin _admins;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public GroupsAdminTests()
    {
        Directory.CreateDirectory(_dir);
        _robust.Add("Fay", "Founder", 0, "p", _founder);
        _groups = new FakeGridGroups(_robust);
        _audit = new AuditLog(Path.Combine(_dir, "audit.log"), TimeProvider.System);
        _joins = new GroupJoins(JoinsFile, null);
        _admins = New();
    }

    private string JoinsFile => Path.Combine(_dir, "group-joins.json");

    private GroupsAdmin New() => new(_groups, _joins, _audit, NullLogger<GroupsAdmin>.Instance);

    public void Dispose()
    {
        _audit.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string Audit()
    {
        var path = Path.Combine(_dir, "audit.log");
        if (!File.Exists(path)) return "";
        // The log keeps the file open while it runs.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd();
    }

    private RobustAccount Resident(string first, string last, int level = 0)
    {
        var id = Guid.NewGuid();
        _robust.Add(first, last, level, "p", id);
        return _robust.All.Single(a => a.PrincipalId == id);
    }

    /// <summary>Makes the joins file unwritable: a folder where it should be (the next save fails).</summary>
    private void BreakJoinsFile()
    {
        if (File.Exists(JoinsFile)) File.Delete(JoinsFile);
        Directory.CreateDirectory(JoinsFile);
        _joins = new GroupJoins(JoinsFile, null);
        _admins = New();
    }

    [Fact]
    public async Task Update_refuses_a_bad_name_without_touching_the_group()
    {
        var g = _groups.AddGroup("Builders", _founder);
        var outcome = await _admins.UpdateAsync(_admin, null, g, "   ", "x", Ct);
        Assert.False(outcome.Done);
        Assert.Equal("Type a group name.", outcome.Message);
        Assert.Equal("Builders", (await _groups.GetAsync(g, Ct))!.Name);
    }

    [Fact]
    public async Task Update_with_a_taken_name_says_so()
    {
        _groups.AddGroup("Builders", _founder);
        var g = _groups.AddGroup("Gardeners", _founder);
        var outcome = await _admins.UpdateAsync(_admin, null, g, "builders", "", Ct);
        Assert.False(outcome.Done);
        Assert.Equal("Another group is already called builders.", outcome.Message);
    }

    [Fact]
    public async Task Update_audits_rename_and_charter_separately()
    {
        var g = _groups.AddGroup("Builders", _founder);
        Assert.True((await _admins.UpdateAsync(_admin, "10.0.0.1", g, "Master Builders", "", Ct)).Done);
        Assert.Contains("\"action\":\"group-renamed\"", Audit());
        Assert.DoesNotContain("group-charter-changed", Audit());
        Assert.True((await _admins.UpdateAsync(_admin, null, g, "Master Builders", "We build.", Ct)).Done);
        Assert.Contains("\"action\":\"group-charter-changed\"", Audit());
        Assert.DoesNotContain("We build.", Audit());
        Assert.Single(Audit().Split('\n'), l => l.Contains("group-renamed"));
    }

    [Fact]
    public async Task Add_remembers_before_adding()
    {
        var g = _groups.AddGroup("Builders", _founder);
        var ann = Resident("Ann", "Smith");
        BreakJoinsFile();
        var outcome = await _admins.AddMemberAsync(_admin, null, g, ann, Ct);
        Assert.False(outcome.Done);
        Assert.Equal("Couldn't save the change. Try again.", outcome.Message);
        Assert.False(_groups.IsMember(g, ann.PrincipalId));
    }

    [Fact]
    public async Task Add_adds_remembers_and_audits()
    {
        var g = _groups.AddGroup("Builders", _founder);
        var ann = Resident("Ann", "Smith");
        var outcome = await _admins.AddMemberAsync(_admin, null, g, ann, Ct);
        Assert.True(outcome.Done, outcome.Message);
        Assert.Contains("about a minute", outcome.Message);
        Assert.True(_groups.IsMember(g, ann.PrincipalId));
        Assert.True(_joins.Has(g, ann.PrincipalId.ToString()));
        Assert.Contains("\"action\":\"group-member-added\"", Audit());
        var again = await _admins.AddMemberAsync(_admin, null, g, ann, Ct);
        Assert.False(again.Done);
        Assert.Equal("Ann Smith is already in Builders.", again.Message);
    }

    [Fact]
    public async Task Removing_the_last_owner_asks_first()
    {
        var g = _groups.AddGroup("Builders", _founder);
        var outcome = await _admins.RemoveMemberAsync(_admin, null, g, _founder.ToString(), confirmed: false, Ct);
        Assert.False(outcome.Done);
        Assert.Equal("Fay Founder is the group's last owner. Removing them leaves the group with no owner. Press Remove again to confirm.", outcome.Message);
        Assert.True(_groups.IsMember(g, _founder));
    }

    [Fact]
    public async Task Removing_the_last_owner_with_confirm_removes()
    {
        var g = _groups.AddGroup("Builders", _founder);
        var outcome = await _admins.RemoveMemberAsync(_admin, null, g, _founder.ToString(), confirmed: true, Ct);
        Assert.True(outcome.Done, outcome.Message);
        Assert.False(_groups.IsMember(g, _founder));
        Assert.Contains("\"action\":\"group-member-removed\"", Audit());
    }

    [Fact]
    public async Task Removing_an_owner_who_is_not_the_last_needs_no_confirm()
    {
        var g = _groups.AddGroup("Builders", _founder);
        var other = Resident("Oli", "Owner");
        _groups.Join(g, other.PrincipalId.ToString(), owner: true);
        Assert.True((await _admins.RemoveMemberAsync(_admin, null, g, _founder.ToString(), confirmed: false, Ct)).Done);
    }

    [Fact]
    public async Task Removing_someone_not_in_the_group_says_so()
    {
        var g = _groups.AddGroup("Builders", _founder);
        var outcome = await _admins.RemoveMemberAsync(_admin, null, g, Guid.NewGuid().ToString(), confirmed: false, Ct);
        Assert.False(outcome.Done);
        Assert.Equal("That person isn't in this group any more.", outcome.Message);
    }

    [Fact]
    public async Task Delete_clears_the_default()
    {
        var g = _groups.AddGroup("Builders", _founder);
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        var outcome = await _admins.DeleteAsync(_admin, null, g, Ct);
        Assert.True(outcome.Done, outcome.Message);
        Assert.False(_groups.Exists(g));
        Assert.Null(_joins.Default());
        Assert.Contains("\"action\":\"group-deleted\"", Audit());
    }

    [Fact]
    public async Task Make_default_records_current_members()
    {
        var g = _groups.AddGroup("Builders", _founder);
        var ann = Resident("Ann", "Smith");
        _groups.Join(g, ann.PrincipalId.ToString());
        Assert.True((await _admins.MakeDefaultAsync(_admin, null, g, Ct)).Done);
        Assert.Equal(g, _joins.Default());
        Assert.Contains("\"action\":\"group-default-set\"", Audit());

        _groups.Leave(g, ann.PrincipalId.ToString()); // Ann leaves in the viewer before anyone presses the button
        var outcome = await _admins.AddEveryoneAsync(_admin, null, Ct);
        Assert.True(outcome.Done, outcome.Message);
        Assert.False(_groups.IsMember(g, ann.PrincipalId));
    }

    [Fact]
    public async Task Clear_default_audits()
    {
        var g = _groups.AddGroup("Builders", _founder);
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        Assert.True((await _admins.ClearDefaultAsync(_admin, null, Ct)).Done);
        Assert.Null(_joins.Default());
        Assert.Contains("\"action\":\"group-default-cleared\"", Audit());
    }

    [Fact]
    public async Task Add_everyone_skips_members_and_remembered_and_counts()
    {
        var g = _groups.AddGroup("Builders", _founder); // the founder is a member
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        var left = Resident("Lee", "Left");
        _joins.Remember(g, [left.PrincipalId.ToString()]); // was in it once, then left
        var newcomer = Resident("Ann", "Smith");
        var waiting = Resident("Pat", "Waiting", level: -1);

        var outcome = await _admins.AddEveryoneAsync(_admin, null, Ct);

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal(1, outcome.Added);
        Assert.Equal(2, outcome.Skipped);
        Assert.Equal("Added 1 residents to Builders; skipped 2 who are already in it or were in it before. It can take about a minute to show in the viewer.", outcome.Message);
        Assert.True(_groups.IsMember(g, newcomer.PrincipalId));
        Assert.False(_groups.IsMember(g, left.PrincipalId));
        Assert.False(_groups.IsMember(g, waiting.PrincipalId));
        Assert.Contains($"{g} added 1 skipped 2", Audit());
    }

    [Fact]
    public async Task Add_everyone_twice_adds_nobody_the_second_time()
    {
        var g = _groups.AddGroup("Builders", _founder);
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        var ann = Resident("Ann", "Smith");
        await _admins.AddEveryoneAsync(_admin, null, Ct);
        _groups.Leave(g, ann.PrincipalId.ToString());
        var again = await _admins.AddEveryoneAsync(_admin, null, Ct);
        Assert.Equal(0, again.Added);
        Assert.False(_groups.IsMember(g, ann.PrincipalId));
    }

    [Fact]
    public async Task Add_everyone_stops_when_the_database_does()
    {
        var g = _groups.AddGroup("Builders", _founder);
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        var people = Enumerable.Range(0, 4).Select(i => Resident("R" + i, "Resident")).ToList();
        // Get, member IDs, active residents, then two adds succeed and the third call fails.
        _groups.FailAfter(5);

        var outcome = await _admins.AddEveryoneAsync(_admin, null, Ct);

        _groups.FailAfter(null);
        Assert.False(outcome.Done);
        Assert.Equal(2, outcome.Added);
        Assert.Equal("Added 2 before the database stopped answering. Press the button again once the world is running.", outcome.Message);
        // Everyone it was about to add is remembered, so a resident who leaves now is never pulled back.
        Assert.All(people, p => Assert.True(_joins.Has(g, p.PrincipalId.ToString())));
    }

    [Fact]
    public async Task Add_everyone_without_a_default_says_so()
    {
        var outcome = await _admins.AddEveryoneAsync(_admin, null, Ct);
        Assert.False(outcome.Done);
        Assert.Equal("There is no default group.", outcome.Message);
    }

    [Fact]
    public async Task Page_actions_with_the_database_down_say_so()
    {
        var g = _groups.AddGroup("Builders", _founder);
        _groups.FailAfter(0);
        var outcome = await _admins.DeleteAsync(_admin, null, g, Ct);
        Assert.False(outcome.Done);
        Assert.Equal(GroupsAdmin.DatabaseDown, outcome.Message);
    }

    [Fact]
    public async Task New_resident_joins_the_default()
    {
        var g = _groups.AddGroup("Builders", _founder);
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        var ann = Resident("Ann", "Smith");
        await _admins.JoinNewResidentAsync(ann, Ct);
        Assert.True(_groups.IsMember(g, ann.PrincipalId));
        Assert.True(_joins.Has(g, ann.PrincipalId.ToString()));
        Assert.Contains("\"action\":\"group-auto-joined\"", Audit());
        Assert.Contains("\"actor\":\"DreamGrid\"", Audit());
    }

    [Fact]
    public async Task New_resident_with_no_default_does_nothing()
    {
        var g = _groups.AddGroup("Builders", _founder);
        var ann = Resident("Ann", "Smith");
        await _admins.JoinNewResidentAsync(ann, Ct);
        Assert.False(_groups.IsMember(g, ann.PrincipalId));
        Assert.DoesNotContain("group-auto-joined", Audit());
    }

    [Fact]
    public async Task New_resident_already_remembered_is_not_added()
    {
        var g = _groups.AddGroup("Builders", _founder);
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        var ann = Resident("Ann", "Smith");
        _joins.Remember(g, [ann.PrincipalId.ToString()]);
        await _admins.JoinNewResidentAsync(ann, Ct);
        Assert.False(_groups.IsMember(g, ann.PrincipalId));
    }

    [Fact]
    public async Task New_resident_when_the_default_was_deleted_in_the_viewer_clears_it()
    {
        var g = _groups.AddGroup("Builders", _founder);
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        _groups.Drop(g);
        await _admins.JoinNewResidentAsync(Resident("Ann", "Smith"), Ct);
        Assert.Null(_joins.Default());
    }

    [Fact]
    public async Task New_resident_when_the_database_is_down_does_not_throw()
    {
        var g = _groups.AddGroup("Builders", _founder);
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        _groups.FailAfter(0);
        await _admins.JoinNewResidentAsync(Resident("Ann", "Smith"), Ct);
    }

    [Fact]
    public async Task New_resident_when_the_joins_file_fails_is_not_added()
    {
        var g = _groups.AddGroup("Builders", _founder);
        await _admins.MakeDefaultAsync(_admin, null, g, Ct);
        var ann = Resident("Ann", "Smith");
        // Keep the default, but make the next save fail.
        File.Delete(JoinsFile);
        Directory.CreateDirectory(JoinsFile);
        await _admins.JoinNewResidentAsync(ann, Ct);
        Assert.False(_groups.IsMember(g, ann.PrincipalId));
    }
}
