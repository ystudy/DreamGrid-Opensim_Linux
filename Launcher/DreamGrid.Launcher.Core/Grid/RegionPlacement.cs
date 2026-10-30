using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>An existing region a new one would touch; <see cref="Side"/> is the side of the existing region it touches.</summary>
public sealed record Neighbour(string Name, string Side);

/// <summary>A region on the grid's map, in squares (not metres); <see cref="Size"/> is its width in squares.</summary>
public sealed record MapRegion(Guid Uuid, string Name, int X, int Y, int Size);

public sealed record PlacementCheck(bool Ok, string? OverlapsWith, IReadOnlyList<Neighbour> Neighbours);

/// <summary>
/// Where a region may go. A region at (x, y) of size n covers squares x..x+n-1 and y..y+n-1 (Y grows north). Two
/// regions are connected exactly when they share an edge; none may overlap another, parked and deleted ones included.
/// </summary>
public static class RegionPlacement
{
    public const int MaxSize = 16;

    /// <summary>The largest map coordinate (the map is 65536 squares on each side).</summary>
    public const int MaxCoordinate = 65535;

    /// <summary>True when a region of <paramref name="size"/> at (x, y) lies wholly on the map: positions run 0 to 65535 minus size.</summary>
    public static bool OnMap(int x, int y, int size) =>
        x >= 0 && y >= 0 && x <= MaxCoordinate - size && y <= MaxCoordinate - size;

    /// <summary>
    /// Whether a region of <paramref name="size"/> at (x, y) fits. <paramref name="elsewhere"/> is the grid's map from
    /// the grid service: those regions count like local ones, except the ones whose UUID is in <c>s.Regions</c> (ours).
    /// </summary>
    public static PlacementCheck Check(LauncherSettings s, int x, int y, int size, Guid? ignore = null,
        IReadOnlyList<MapRegion>? elsewhere = null)
    {
        var existing = s.Regions.Where(r => r.Uuid != ignore).Select(r => (r.Name, r.X, r.Y, r.Size)).ToList();
        if (elsewhere is not null)
        {
            var ours = s.Regions.Select(r => r.Uuid).ToHashSet();
            existing.AddRange(elsewhere.Where(m => !ours.Contains(m.Uuid)).Select(m => (m.Name, m.X, m.Y, m.Size)));
        }

        var neighbours = new List<Neighbour>();
        foreach (var (name, ex, ey, en) in existing)
        {
            var xOverlap = x < ex + en && ex < x + size;
            var yOverlap = y < ey + en && ey < y + size;
            if (xOverlap && yOverlap) return new PlacementCheck(false, name, []);
            if (yOverlap && x == ex + en) neighbours.Add(new Neighbour(name, "east"));
            else if (yOverlap && x + size == ex) neighbours.Add(new Neighbour(name, "west"));
            else if (xOverlap && y == ey + en) neighbours.Add(new Neighbour(name, "north"));
            else if (xOverlap && y + size == ey) neighbours.Add(new Neighbour(name, "south"));
        }
        return new PlacementCheck(true, null, neighbours);
    }

    /// <summary>
    /// The first spot where a region of <paramref name="size"/> fits: rows y = 1000, 1001, …, and within each row
    /// x = 1000 … 1063. If every scanned square is taken (practically impossible) the rows keep going until the
    /// edge of the map; if the whole map is full it throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static (int X, int Y) FirstFree(LauncherSettings s, IReadOnlyList<MapRegion> elsewhere, int size = 1)
    {
        for (var y = 1000; y <= MaxCoordinate - size; y++)
            for (var x = 1000; x < 1064; x++)
                if (OnMap(x, y, size) && Check(s, x, y, size, elsewhere: elsewhere).Ok) return (x, y);
        throw new InvalidOperationException("There is no free place left on the map.");
    }
}
