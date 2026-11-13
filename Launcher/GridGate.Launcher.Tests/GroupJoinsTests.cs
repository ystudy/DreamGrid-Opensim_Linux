using GridGate.Launcher.Core.Groups;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The default group and who the launcher knows has been in each group (<c>guests/group-joins.json</c>).</summary>
public sealed class GroupJoinsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "group-joins-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "group-joins.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Starts_with_no_default() => Assert.Null(new GroupJoins(File, null).Default());

    [Fact]
    public void Set_default_records_current_members()
    {
        var group = Guid.NewGuid();
        string a = Guid.NewGuid().ToString(), b = Guid.NewGuid().ToString();
        Assert.True(new GroupJoins(File, null).SetDefault(group, [a, b]));

        var again = new GroupJoins(File, null);
        Assert.Equal(group, again.Default());
        Assert.True(again.Has(group, a));
        Assert.True(again.Has(group, b));
        Assert.False(again.Has(group, Guid.NewGuid().ToString()));
    }

    [Fact]
    public void Remember_is_case_insensitive()
    {
        var group = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();
        var joins = new GroupJoins(File, null);
        Assert.True(joins.Remember(group, [id.ToUpperInvariant()]));
        Assert.True(joins.Has(group, id));
        Assert.True(new GroupJoins(File, null).Has(group, id.ToUpperInvariant()));
    }

    [Fact]
    public void Clear_default_keeps_the_list()
    {
        var group = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();
        var joins = new GroupJoins(File, null);
        joins.SetDefault(group, [id]);
        Assert.True(joins.ClearDefault());
        Assert.Null(joins.Default());
        Assert.True(joins.Has(group, id));
    }

    [Fact]
    public void Group_deleted_drops_its_list_and_clears_the_default()
    {
        Guid group = Guid.NewGuid(), other = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();
        var joins = new GroupJoins(File, null);
        joins.SetDefault(group, [id]);
        joins.Remember(other, [id]);
        Assert.True(joins.GroupDeleted(group));
        Assert.Null(joins.Default());
        Assert.False(joins.Has(group, id));
        Assert.True(joins.Has(other, id));
    }

    [Fact]
    public void Deleting_another_group_keeps_the_default()
    {
        Guid group = Guid.NewGuid(), other = Guid.NewGuid();
        var joins = new GroupJoins(File, null);
        joins.SetDefault(group, []);
        joins.GroupDeleted(other);
        Assert.Equal(group, joins.Default());
    }

    [Fact]
    public void Failed_save_remembers_nothing()
    {
        Directory.CreateDirectory(File); // a folder where the file should be: every save fails
        var joins = new GroupJoins(File, null);
        var group = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();
        Assert.False(joins.Remember(group, [id]));
        Assert.False(joins.Has(group, id));
        Assert.False(joins.SetDefault(group, [id]));
        Assert.Null(joins.Default());
    }
}
