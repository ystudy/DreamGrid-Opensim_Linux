using System.Formats.Tar;
using System.IO.Compression;

namespace DreamGrid.Launcher.Core.Grid;

public static class OarFile
{
    /// <summary>True when the file is a gzip tar whose first entry is <c>archive.xml</c>; it reads only the start of the file.</summary>
    public static bool LooksLikeOar(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            return tar.GetNextEntry(copyData: false)?.Name == "archive.xml";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
        {
            return false;
        }
    }
}
