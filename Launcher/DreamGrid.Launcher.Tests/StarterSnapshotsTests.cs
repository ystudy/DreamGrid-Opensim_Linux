using DreamGrid.Launcher.Core.Starter;
using SkiaSharp;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Starter avatar snapshots, re-encoded (spec 2026-11-11-starter-avatars-design §6.3).</summary>
public sealed class StarterSnapshotsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dg-snap-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    internal static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void Snapshot_png_becomes_webp_800_wide()
    {
        var id = Guid.NewGuid();
        var name = StarterSnapshots.Save(new MemoryStream(Png(1600, 1200)), _dir, id, out var problem);
        Assert.Null(problem);
        Assert.Equal(id + ".webp", name);
        using var codec = SKCodec.Create(Path.Combine(_dir, name!));
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal((800, 600), (codec.Info.Width, codec.Info.Height));
    }

    [Fact]
    public void Small_snapshot_keeps_its_size_and_a_suggestion_has_its_own_name()
    {
        var id = Guid.NewGuid();
        var name = StarterSnapshots.Save(new MemoryStream(Png(300, 400)), _dir, id, out _, suggested: true);
        Assert.Equal(id + ".suggested.webp", name);
        using var codec = SKCodec.Create(Path.Combine(_dir, name!));
        Assert.Equal((300, 400), (codec.Info.Width, codec.Info.Height));
    }

    [Fact]
    public void Non_image_is_refused()
    {
        Assert.Null(StarterSnapshots.Save(new MemoryStream("not a picture at all"u8.ToArray()), _dir, Guid.NewGuid(), out var problem));
        Assert.Equal(StarterSnapshots.NotAPicture, problem);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Over_5_mb_is_refused()
    {
        var big = new byte[StarterSnapshots.MaxBytes + 1];
        Png(10, 10).CopyTo(big, 0);
        Assert.Null(StarterSnapshots.Save(new MemoryStream(big), _dir, Guid.NewGuid(), out var problem));
        Assert.Equal(StarterSnapshots.TooBig, problem);
    }

    [Fact]
    public void Huge_dimensions_are_refused_before_decoding()
    {
        // A PNG header claiming 20000 × 20000 with no pixel data: refused by its size alone, never decoded.
        var png = Png(1, 1);
        png[16] = 0; png[17] = 0; png[18] = 0x4E; png[19] = 0x20; // width 20000
        png[20] = 0; png[21] = 0; png[22] = 0x4E; png[23] = 0x20; // height 20000
        var crc = Crc32(png.AsSpan(12, 17)); // IHDR's type and data
        png[29] = (byte)(crc >> 24); png[30] = (byte)(crc >> 16); png[31] = (byte)(crc >> 8); png[32] = (byte)crc;
        Assert.Null(StarterSnapshots.Save(new MemoryStream(png), _dir, Guid.NewGuid(), out var problem));
        Assert.Equal(StarterSnapshots.TooManyPixels, problem);
    }
}
