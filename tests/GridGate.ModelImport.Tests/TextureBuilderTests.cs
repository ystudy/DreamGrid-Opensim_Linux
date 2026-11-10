using System.Drawing;
using System.Drawing.Imaging;
using GridGate.ModelImport.Building;
using GridGate.ModelImport.Model;
using Xunit;

namespace GridGate.ModelImport.Tests;

public class TextureBuilderTests
{
    private static ImportImage Image(int width, int height, ImageFormat? format = null, string mime = "image/png")
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.CornflowerBlue);
        using var ms = new MemoryStream();
        bitmap.Save(ms, format ?? ImageFormat.Png);
        return new ImportImage("tex", mime, ms.ToArray());
    }

    [Fact]
    public void Power_of_two_under_cap_is_unchanged_size()
    {
        var r = TextureBuilder.Build(Image(512, 256), 1024);
        Assert.Equal((512, 256), (r.Width, r.Height));
        Assert.Null(r.Finding);
        Assert.NotEmpty(r.J2k);
    }

    [Fact]
    public void Odd_size_goes_to_nearest_power_of_two()
    {
        var r = TextureBuilder.Build(Image(300, 200), 1024);
        Assert.Equal((256, 256), (r.Width, r.Height));
        Assert.Equal("texture-resized", r.Finding!.Code);
        Assert.Equal(FindingKind.Safe, r.Finding.Kind);
        Assert.Equal("tex", r.Finding.Values["image"]);
    }

    [Fact]
    public void Ties_round_up()
    {
        var r = TextureBuilder.Build(Image(384, 96), 1024);
        Assert.Equal((512, 128), (r.Width, r.Height));
    }

    [Fact]
    public void Over_1024_is_shrunk_and_risky()
    {
        var r = TextureBuilder.Build(Image(2048, 2048), 1024);
        Assert.Equal((1024, 1024), (r.Width, r.Height));
        Assert.Equal("texture-shrunk", r.Finding!.Code);
        Assert.Equal(FindingKind.Risky, r.Finding.Kind);
        Assert.Equal("2048", r.Finding.Values["from"]);
        Assert.Equal("1024", r.Finding.Values["to"]);
        Assert.Equal("tex", r.Finding.Values["image"]);
    }

    [Fact]
    public void Output_is_jpeg2000()
    {
        var r = TextureBuilder.Build(Image(64, 64), 1024);
        Assert.Equal(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }, r.J2k[..4]);
    }

    [Fact]
    public void Jpeg_is_decoded_by_header_not_by_declared_type()
    {
        var r = TextureBuilder.Build(Image(64, 32, ImageFormat.Jpeg, "image/webp"), 1024);
        Assert.Equal((64, 32), (r.Width, r.Height));
        Assert.NotEmpty(r.J2k);
    }

    [Fact]
    public void Webp_is_unsupported()
    {
        var webp = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', 0, 0, 0, 0 };
        var r = TextureBuilder.Build(new ImportImage("w", "image/png", webp), 1024);
        Assert.Empty(r.J2k);
        Assert.Equal("texture-unsupported", r.Finding!.Code);
        Assert.Equal(FindingKind.Risky, r.Finding.Kind);
    }

    [Fact]
    public void Corrupt_png_is_unsupported_not_a_crash()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 8, 0, 0, 0, 8, 8, 2 };
        var r = TextureBuilder.Build(new ImportImage("c", "image/png", png), 1024);
        Assert.Empty(r.J2k);
        Assert.Equal("texture-unsupported", r.Finding!.Code);
    }
}
