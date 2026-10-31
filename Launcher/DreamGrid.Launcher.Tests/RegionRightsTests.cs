using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionRightsTests : IDisposable
{
    private static readonly Guid Beach = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid Isle = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-rights-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string File_ => Path.Combine(_dir, "guests", "region-rights.json");

    private RegionRights New() => new(File_, _time);

    [Fact]
    public void Grant_sets_pending_to_owner()
    {
        var rights = New();

        rights.Grant(Beach, Ann, "Ann Guest");

        var r = rights.Of(Beach)!;
        Assert.Equal((Beach, Ann, "Ann Guest", _time.GetUtcNow(), EstateMove.ToOwner), (r.Region, r.Owner, r.OwnerName, r.Since, r.Pending));
        Assert.True(r.Held);
        Assert.Equal([Beach], rights.OwnedBy(Ann).Select(x => x.Region));
        Assert.Equal([Beach], rights.PendingMoves().Select(x => x.Region));
        Assert.Null(rights.Of(Isle));
    }

    [Fact]
    public void Grant_replaces_previous_owner()
    {
        var rights = New();
        rights.Grant(Beach, Ann, "Ann Guest");
        rights.MoveDone(Beach);
        _time.Advance(TimeSpan.FromDays(1));

        rights.Grant(Beach, Bob, "Bob Guest");

        var r = rights.Of(Beach)!;
        Assert.Equal((Bob, EstateMove.ToOwner, _time.GetUtcNow()), (r.Owner, r.Pending, r.Since));
        Assert.Empty(rights.OwnedBy(Ann));
        Assert.Single(rights.OwnedBy(Bob));
    }

    [Fact]
    public void Remove_sets_pending_to_grid()
    {
        var rights = New();
        rights.Grant(Beach, Ann, "Ann Guest");
        rights.MoveDone(Beach); // it sits in Ann's estate now

        rights.Remove(Beach);

        var r = rights.Of(Beach)!;
        Assert.Equal(EstateMove.ToGrid, r.Pending);
        Assert.False(r.Held);
        // The person has nothing from now on, although the region still has to move back.
        Assert.Empty(rights.OwnedBy(Ann));
        Assert.Equal([Beach], rights.PendingMoves().Select(x => x.Region));
    }

    [Fact]
    public void Remove_before_the_first_move_drops_the_entry_at_once()
    {
        var rights = New();
        rights.Grant(Beach, Ann, "Ann Guest");

        rights.Remove(Beach);

        Assert.Null(rights.Of(Beach));
        Assert.Empty(rights.PendingMoves());
    }

    [Fact]
    public void Remove_before_the_move_but_after_an_earlier_one_goes_back_to_the_grid()
    {
        var rights = New();
        rights.Grant(Beach, Ann, "Ann Guest");
        rights.SetHomeEstate(Beach, 1); // it was moved out of the grid's estate once
        rights.MoveDone(Beach);
        rights.Grant(Beach, Bob, "Bob Guest"); // handed on, the move to Bob's estate not done yet

        rights.Remove(Beach);

        Assert.Equal(EstateMove.ToGrid, rights.Of(Beach)!.Pending);
        Assert.Equal(1, rights.HomeEstateOf(Beach));
    }

    [Fact]
    public void Remove_without_a_right_does_nothing()
    {
        var rights = New();

        rights.Remove(Beach);

        Assert.Null(rights.Of(Beach));
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void Moves_complete_and_the_entry_goes_when_it_is_back_in_the_grids_estate()
    {
        var rights = New();
        rights.Grant(Beach, Ann, "Ann Guest");
        rights.SetHomeEstate(Beach, 1);

        rights.MoveDone(Beach);
        Assert.Null(rights.Of(Beach)!.Pending);
        Assert.Empty(rights.PendingMoves());
        Assert.Equal(1, rights.HomeEstateOf(Beach));

        rights.Remove(Beach);
        rights.MoveDone(Beach);
        Assert.Null(rights.Of(Beach));
        Assert.Null(rights.HomeEstateOf(Beach));
    }

    [Fact]
    public void Estate_per_owner_reused_across_regions()
    {
        var rights = New();
        Assert.Null(rights.EstateOf(Ann));

        rights.SetEstate(Ann, 101, "Ann Guest's estate");
        rights.Grant(Beach, Ann, "Ann Guest");
        rights.Grant(Isle, Ann, "Ann Guest");

        Assert.Equal(101, rights.EstateOf(Ann));
        Assert.Null(rights.EstateOf(Bob));
        Assert.Equal(2, rights.OwnedBy(Ann).Count);
        // Taking the rights away does not forget the person's estate.
        rights.Remove(Beach);
        Assert.Equal(101, rights.EstateOf(Ann));
    }

    [Fact]
    public void Forget_drops_entry()
    {
        var rights = New();
        rights.Grant(Beach, Ann, "Ann Guest");
        rights.Grant(Isle, Ann, "Ann Guest");
        rights.SetHomeEstate(Beach, 1);

        rights.Forget(Beach);

        Assert.Null(rights.Of(Beach));
        Assert.Null(rights.HomeEstateOf(Beach));
        Assert.Equal([Isle], rights.OwnedBy(Ann).Select(x => x.Region));
        Assert.Null(New().Of(Beach));
    }

    [Fact]
    public void Damaged_file_means_no_rights()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "{ this is not json");

        var rights = New();

        Assert.Null(rights.Of(Beach));
        Assert.Empty(rights.OwnedBy(Ann));
        Assert.Null(rights.EstateOf(Ann));
        Assert.Empty(rights.PendingMoves());
        // The damaged file is set aside, not read as anything.
        Assert.False(File.Exists(File_));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(File_)!, "region-rights.json.damaged-*"));
    }

    [Fact]
    public void Survives_restart()
    {
        var first = New();
        first.Grant(Beach, Ann, "Ann Guest");
        first.SetEstate(Ann, 101, "Ann Guest's estate");
        first.SetHomeEstate(Beach, 1);
        first.Grant(Isle, Bob, "Bob Guest");
        first.MoveDone(Isle);
        first.Remove(Isle);

        var again = New();

        var beach = again.Of(Beach)!;
        Assert.Equal((Ann, "Ann Guest", EstateMove.ToOwner, _time.GetUtcNow()), (beach.Owner, beach.OwnerName, beach.Pending, beach.Since));
        Assert.Equal(101, again.EstateOf(Ann));
        Assert.Equal(1, again.HomeEstateOf(Beach));
        Assert.Equal(EstateMove.ToGrid, again.Of(Isle)!.Pending);
        Assert.False(again.Of(Isle)!.Held);
        Assert.Empty(again.OwnedBy(Bob));
        Assert.Equal(2, again.PendingMoves().Count);
    }
}
