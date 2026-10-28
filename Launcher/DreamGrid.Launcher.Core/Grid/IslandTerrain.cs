namespace DreamGrid.Launcher.Core.Grid;

/// <summary>The default island heightmap (see <see cref="TerrainShapes"/>), as setup writes it.</summary>
public static class IslandTerrain
{
    public static void WriteR32(string path, int size = 256, int seed = 1) =>
        TerrainShapes.WriteR32(path, TerrainShape.Island, size, seed);
}
