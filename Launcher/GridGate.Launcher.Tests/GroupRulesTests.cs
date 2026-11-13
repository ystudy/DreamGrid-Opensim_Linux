using GridGate.Launcher.Core.Groups;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Group names and charters an admin may save, and how a member's ID is read (spec 2026-11-02-groups-admin-design).</summary>
public sealed class GroupRulesTests
{
    private const string Visitor = "6a0f3c2e-1d55-4c1b-9b9e-0d8a4f3b2c11;https://grid.example:8002/;Ann Smith";

    [Theory]
    [InlineData("", "Type a group name.")]
    [InlineData("   ", "Type a group name.")]
    [InlineData(null, "Type a group name.")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A group name can be at most 35 characters.")]
    [InlineData("a\tb", "A group name can't contain line breaks or tabs.")]
    [InlineData("a\nb", "A group name can't contain line breaks or tabs.")]
    [InlineData("Builders 🔨", "A group name can't contain emoji or other symbols the grid can't store.")]
    [InlineData("Café Société", null)]
    [InlineData("  Builders  ", null)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)]
    public void Name_problem(string? name, string? expected) => Assert.Equal(expected, GroupRules.NameProblem(name));

    [Fact]
    public void Charter_over_4000_is_refused()
    {
        Assert.Null(GroupRules.CharterProblem(new string('c', 4000)));
        Assert.Null(GroupRules.CharterProblem(null));
        Assert.Equal("The charter can be at most 4000 characters.", GroupRules.CharterProblem(new string('c', 4001)));
    }

    [Fact]
    public void Charter_with_emoji_is_refused()
    {
        // The columns are 3-byte utf8: MariaDB refuses a 4-byte character, which would read as the database being down.
        Assert.Equal("The charter can't contain emoji or other symbols the grid can't store.", GroupRules.CharterProblem("We build 🏗 things."));
        Assert.Null(GroupRules.CharterProblem("Ünïcödé — fine…"));
    }

    [Fact]
    public void Plain_uuid_is_local()
    {
        var id = Guid.NewGuid();
        Assert.True(GroupRules.IsLocal(id.ToString(), out var read));
        Assert.Equal(id, read);
    }

    [Fact]
    public void Hypergrid_id_is_not_local() => Assert.False(GroupRules.IsLocal(Visitor, out _));

    [Fact]
    public void Display_name_of_a_visitor() => Assert.Equal("Ann Smith (visitor)", GroupRules.DisplayName(Visitor, null, null));

    [Fact]
    public void Display_name_of_a_resident() =>
        Assert.Equal("Bob Jones", GroupRules.DisplayName(Guid.NewGuid().ToString(), "Bob", "Jones"));

    [Fact]
    public void Display_name_of_a_missing_account_is_its_id()
    {
        var id = Guid.NewGuid().ToString();
        Assert.Equal(id, GroupRules.DisplayName(id, null, null));
    }

    [Fact]
    public void Everyone_is_all_zeros() => Assert.Equal(Guid.Empty, GroupRules.Everyone);
}
