using System.Formats.Tar;
using System.IO.Compression;
using DreamGrid.Launcher.Core.Grid;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class OarFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-oarfile-" + Guid.NewGuid().ToString("N"));

    public OarFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string TarGz(string name, params string[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip);
        foreach (var entry in entries)
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry) { DataStream = new MemoryStream("<x/>"u8.ToArray()) });
        return path;
    }

    [Fact]
    public void LooksLikeOar_wants_archive_xml_first()
    {
        Assert.True(OarFile.LooksLikeOar(TarGz("good.oar", "archive.xml", "terrains/x.r32")));
        Assert.False(OarFile.LooksLikeOar(TarGz("wrong-first.oar", "terrains/x.r32", "archive.xml")));
    }

    [Fact]
    public void LooksLikeOar_refuses_a_zip_an_empty_file_and_plain_text()
    {
        var zip = Path.Combine(_dir, "a.oar");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            z.CreateEntry("archive.xml");
        var empty = Path.Combine(_dir, "empty.oar");
        File.WriteAllBytes(empty, []);
        var text = Path.Combine(_dir, "text.oar");
        File.WriteAllText(text, "this is not an archive at all");

        Assert.False(OarFile.LooksLikeOar(zip));
        Assert.False(OarFile.LooksLikeOar(empty));
        Assert.False(OarFile.LooksLikeOar(text));
    }

    [Fact]
    public void LooksLikeOar_refuses_a_gzip_that_is_not_a_tar()
    {
        var path = Path.Combine(_dir, "gz.oar");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            gzip.Write("plain text, not a tar archive"u8);

        Assert.False(OarFile.LooksLikeOar(path));
    }

    [Fact]
    public void LooksLikeOar_refuses_a_missing_file() => Assert.False(OarFile.LooksLikeOar(Path.Combine(_dir, "none.oar")));
}
