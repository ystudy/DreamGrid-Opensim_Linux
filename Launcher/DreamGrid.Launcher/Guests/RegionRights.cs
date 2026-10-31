using System.Text.Json.Serialization;
using DreamGrid.Launcher.Core;

namespace DreamGrid.Launcher.Guests;

/// <summary>Which estate a region still has to move into: its owner's (<see cref="ToOwner"/>) or back to the grid's (<see cref="ToGrid"/>).</summary>
public enum EstateMove { ToOwner, ToGrid }

/// <summary>
/// One guest's right over one region: who, since when, and the estate move still to do when the region next runs
/// (<c>null</c> when the region is where it belongs). A right being taken away stays here with <see cref="EstateMove.ToGrid"/>
/// until the region has moved back; it gives its person nothing from then on (<see cref="Held"/>).
/// </summary>
public sealed record RegionRight(Guid Region, Guid Owner, string OwnerName, DateTimeOffset Since, EstateMove? Pending)
{
    /// <summary>Whether the person has the right now; false while it is only waiting for the region to move back.</summary>
    [JsonIgnore] public bool Held => Pending != EstateMove.ToGrid;
}

/// <summary>The estate a guest owns on this grid, made the first time one of their regions moves; every region of theirs goes into it.</summary>
public sealed record OwnerEstate(Guid Owner, int EstateId, string Name);

/// <summary>The estate a region was in before it moved into its owner's, so it can go back there.</summary>
public sealed record HomeEstate(Guid Region, int EstateId);

/// <summary>What <c>region-rights.json</c> holds.</summary>
public sealed class RegionRightsData
{
    public List<RegionRight> Rights { get; set; } = [];
    public List<OwnerEstate> Estates { get; set; } = [];
    public List<HomeEstate> Homes { get; set; } = [];
}

/// <summary>
/// Which guest has rights over which region, the estate each guest owns and the estate each moved region came from. Kept in
/// <c>file</c> (<c>&lt;data&gt;/guests/region-rights.json</c>); a missing or damaged file means nobody has any right
/// (<see cref="JsonStateFile{T}"/> sets a damaged one aside), so a fault never gives anyone a region. Safe to call from any thread.
/// </summary>
public sealed class RegionRights
{
    private readonly JsonStateFile<RegionRightsData> _file;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly RegionRightsData _data;

    public RegionRights(string file, TimeProvider time, ILogger<RegionRights>? log = null)
    {
        _time = time;
        _file = new JsonStateFile<RegionRightsData>(file, () => new RegionRightsData(), log);
        _data = _file.Load();
    }

    /// <summary>
    /// The region's right, or null. It can be one that is being taken away (<see cref="RegionRight.Held"/> false, waiting for the
    /// region to move back); check <see cref="RegionRight.Held"/> before letting anyone act on it.
    /// </summary>
    public RegionRight? Of(Guid region)
    {
        lock (_gate) return _data.Rights.FirstOrDefault(r => r.Region == region);
    }

    /// <summary>The regions the person has rights over now (rights being taken away don't count).</summary>
    public IReadOnlyList<RegionRight> OwnedBy(Guid owner)
    {
        lock (_gate) return _data.Rights.Where(r => r.Owner == owner && r.Held).ToList();
    }

    /// <summary>Gives the person the region, replacing any earlier owner; the region moves into their estate when it next runs.</summary>
    public void Grant(Guid region, Guid owner, string ownerName)
    {
        lock (_gate)
        {
            _data.Rights.RemoveAll(r => r.Region == region);
            _data.Rights.Add(new RegionRight(region, owner, ownerName, _time.GetUtcNow(), EstateMove.ToOwner));
            Save();
        }
    }

    /// <summary>
    /// Takes the right away: the region moves back to the grid's estate when it next runs, and the entry stays until it has
    /// (<see cref="MoveDone"/>). A right whose region never moved has nothing to move back and is dropped at once. Does nothing
    /// when the region has no right.
    /// </summary>
    public void Remove(Guid region)
    {
        lock (_gate)
        {
            var at = _data.Rights.FindIndex(r => r.Region == region);
            if (at < 0) return;
            var right = _data.Rights[at];
            if (right.Pending == EstateMove.ToOwner && !_data.Homes.Any(h => h.Region == region)) _data.Rights.RemoveAt(at);
            else _data.Rights[at] = right with { Pending = EstateMove.ToGrid };
            Save();
        }
    }

    /// <summary>Drops everything about the region (it is erased for good).</summary>
    public void Forget(Guid region)
    {
        lock (_gate)
        {
            var changed = _data.Rights.RemoveAll(r => r.Region == region) + _data.Homes.RemoveAll(h => h.Region == region);
            if (changed > 0) Save();
        }
    }

    /// <summary>The estate the person owns on this grid, or null before their first region moved.</summary>
    public int? EstateOf(Guid owner)
    {
        lock (_gate) return _data.Estates.FirstOrDefault(e => e.Owner == owner)?.EstateId;
    }

    /// <summary>The estate the person owns on this grid with its name, or null before their first region moved.</summary>
    public OwnerEstate? OwnerEstateOf(Guid owner)
    {
        lock (_gate) return _data.Estates.FirstOrDefault(e => e.Owner == owner);
    }

    /// <summary>Remembers the estate the person owns; their later regions go into it.</summary>
    public void SetEstate(Guid owner, int estateId, string name)
    {
        lock (_gate)
        {
            _data.Estates.RemoveAll(e => e.Owner == owner);
            _data.Estates.Add(new OwnerEstate(owner, estateId, name));
            Save();
        }
    }

    /// <summary>The estate the region was in before it moved into its owner's, or null when it has not moved.</summary>
    public int? HomeEstateOf(Guid region)
    {
        lock (_gate) return _data.Homes.FirstOrDefault(h => h.Region == region)?.EstateId;
    }

    /// <summary>Remembers the estate the region leaves, so it can go back.</summary>
    public void SetHomeEstate(Guid region, int estateId)
    {
        lock (_gate)
        {
            _data.Homes.RemoveAll(h => h.Region == region);
            _data.Homes.Add(new HomeEstate(region, estateId));
            Save();
        }
    }

    /// <summary>
    /// The pending move is done. A region that moved into its owner's estate has nothing left to do; one that moved back to the
    /// grid's loses its entry and the estate it came from.
    /// </summary>
    public void MoveDone(Guid region) => MoveDone(region, null);

    /// <summary>
    /// As <see cref="MoveDone(Guid)"/> for the move <paramref name="right"/> asked for, when the region's right is still that one;
    /// a right taken away or handed on meanwhile keeps its own move.
    /// </summary>
    public void MoveDone(RegionRight right) => MoveDone(right.Region, right);

    private void MoveDone(Guid region, RegionRight? expected)
    {
        lock (_gate)
        {
            var at = _data.Rights.FindIndex(r => r.Region == region);
            if (at < 0 || _data.Rights[at].Pending is null) return;
            if (expected is not null && _data.Rights[at] != expected) return;
            if (_data.Rights[at].Pending == EstateMove.ToGrid)
            {
                _data.Rights.RemoveAt(at);
                _data.Homes.RemoveAll(h => h.Region == region);
            }
            else
            {
                _data.Rights[at] = _data.Rights[at] with { Pending = null };
            }
            Save();
        }
    }

    /// <summary>The rights whose region still has to move, either way.</summary>
    public IReadOnlyList<RegionRight> PendingMoves()
    {
        lock (_gate) return _data.Rights.Where(r => r.Pending is not null).ToList();
    }

    private void Save() => _file.Save(_data);
}
