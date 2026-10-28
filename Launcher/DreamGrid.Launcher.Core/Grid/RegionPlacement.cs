using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>An existing region a new one would touch; <see cref="Side"/> is the side of the existing region it touches.</summary>
public sealed record Neighbour(string Name, string Side);

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

    public static PlacementCheck Check(LauncherSettings s, int x, int y, int size, Guid? ignore = null)
    {
        var neighbours = new List<Neighbour>();
        foreach (var r in s.Regions)
        {
            if (r.Uuid == ignore) continue;
            var (ex, ey, en) = (r.X, r.Y, r.Size);
            var xOverlap = x < ex + en && ex < x + size;
            var yOverlap = y < ey + en && ey < y + size;
            if (xOverlap && yOverlap) return new PlacementCheck(false, r.Name, []);
            if (yOverlap && x == ex + en) neighbours.Add(new Neighbour(r.Name, "east"));
            else if (yOverlap && x + size == ex) neighbours.Add(new Neighbour(r.Name, "west"));
            else if (xOverlap && y == ey + en) neighbours.Add(new Neighbour(r.Name, "north"));
            else if (xOverlap && y + size == ey) neighbours.Add(new Neighbour(r.Name, "south"));
        }
        return new PlacementCheck(true, null, neighbours);
    }
}
