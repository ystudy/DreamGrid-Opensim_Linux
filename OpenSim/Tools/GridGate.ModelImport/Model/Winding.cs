namespace GridGate.ModelImport.Model;

public static class Winding
{
    /// <summary>A copy of a triangle list with every triangle's winding reversed (its second and third indices swapped).</summary>
    public static int[] Reversed(int[] triangles)
    {
        var copy = (int[])triangles.Clone();
        for (int i = 0; i + 2 < copy.Length; i += 3) (copy[i + 1], copy[i + 2]) = (copy[i + 2], copy[i + 1]);
        return copy;
    }
}
