using System.Text;
using DreamGrid.Launcher.Core.Pictures;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Uploaded pictures: checked by their contents, redrawn under a new name, kept with a small index (spec §4.4).</summary>
public sealed class PictureStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dg-pictures-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private PictureStore Store(long? maxPixels = null) =>
        maxPixels is { } m ? new PictureStore(_folder, _time) { MaxPixels = m } : new PictureStore(_folder, _time);

    private static Task<PictureResult> AddAsync(PictureStore store, byte[] bytes, string? label = "test.png") =>
        store.AddAsync(new MemoryStream(bytes), label, "Test Owner", Ct);

    private string[] PictureFiles() =>
        Directory.Exists(_folder) ? Directory.GetFiles(_folder).Where(f => !f.EndsWith("index.json", StringComparison.Ordinal)).ToArray() : [];

    private string[] PartFiles() =>
        Directory.Exists(Path.Combine(_folder, ".incoming")) ? Directory.GetFiles(Path.Combine(_folder, ".incoming")) : [];

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpg", "image/jpeg")]
    [InlineData("webp", "image/webp")]
    public async Task Png_jpeg_and_webp_are_kept_redrawn(string ext, string type)
    {
        // A PNG redraws losslessly (Skia's own PNG would come back the same bytes), so it carries an extra chunk that must go.
        var bytes = ext switch { "png" => TestPictures.PngWithText(64, 48), "jpg" => TestPictures.Jpeg(64, 48), _ => TestPictures.Webp(64, 48) };
        var store = Store();

        var added = Assert.IsType<PictureResult.Added>(await AddAsync(store, bytes));

        Assert.True(PictureStore.IsName(added.Picture.Name));
        Assert.EndsWith("." + ext, added.Picture.Name);
        var (path, contentType) = store.Open(added.Picture.Name)!.Value;
        Assert.Equal(type, contentType);
        Assert.NotEqual(bytes, File.ReadAllBytes(path));
        Assert.DoesNotContain("secret note", Encoding.ASCII.GetString(File.ReadAllBytes(path)));
        Assert.Equal((64, 48), (added.Picture.Width, added.Picture.Height));
        Assert.Equal("Test Owner", added.Picture.AddedBy);
        Assert.True(store.Exists(added.Picture.Name));
    }

    public static TheoryData<string> NotPictures => ["html", "svg", "gif", "truncated"];

    [Theory]
    [MemberData(nameof(NotPictures))]
    public async Task Wrong_content_is_refused_and_nothing_kept(string kind)
    {
        var bytes = kind switch
        {
            "html" => Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"),
            "svg" => Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"),
            "gif" => TestPictures.Gif(),
            _ => TestPictures.Jpeg(200, 200)[..300],
        };
        var store = Store();

        Assert.IsType<PictureResult.Refused>(await AddAsync(store, bytes));

        Assert.Empty(PictureFiles());
        Assert.Empty(PartFiles());
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Exif_is_gone_after_redrawing()
    {
        var source = TestPictures.JpegWithExif(64, 64);
        Assert.Contains("GPSLatitude", Encoding.ASCII.GetString(source));
        var store = Store();

        var added = Assert.IsType<PictureResult.Added>(await AddAsync(store, source, "holiday.jpg"));

        var stored = Encoding.ASCII.GetString(File.ReadAllBytes(store.Open(added.Picture.Name)!.Value.Path));
        Assert.DoesNotContain("Exif", stored);
        Assert.DoesNotContain("GPSLatitude", stored);
    }

    [Fact]
    public async Task Display_p3_colours_are_kept_as_seen()
    {
        // A phone photo in Display P3: redrawn, it must look the same in a browser (converted to sRGB, not reinterpreted).
        var source = TestPictures.PngDisplayP3(16, 16, 204, 77, 51);
        var seen = TestPictures.FirstPixelInSrgb(source);
        var store = Store();

        var added = Assert.IsType<PictureResult.Added>(await AddAsync(store, source, "phone.png"));

        var stored = TestPictures.FirstPixelInSrgb(File.ReadAllBytes(store.Open(added.Picture.Name)!.Value.Path));
        Assert.True(Math.Abs(seen.Red - stored.Red) <= 3 && Math.Abs(seen.Green - stored.Green) <= 3 && Math.Abs(seen.Blue - stored.Blue) <= 3,
            $"seen {seen}, stored {stored}");
    }

    [Fact]
    public async Task Big_pictures_are_scaled_to_3840()
    {
        var store = Store();

        var big = Assert.IsType<PictureResult.Added>(await AddAsync(store, TestPictures.Png(5000, 1000)));
        var small = Assert.IsType<PictureResult.Added>(await AddAsync(store, TestPictures.Png(800, 600)));

        Assert.Equal((3840, 768), (big.Picture.Width, big.Picture.Height));
        using (var codec = SKCodec.Create(store.Open(big.Picture.Name)!.Value.Path))
            Assert.Equal((3840, 768), (codec.Info.Width, codec.Info.Height));
        Assert.Equal((800, 600), (small.Picture.Width, small.Picture.Height));
    }

    [Fact]
    public async Task Too_many_pixels_is_refused_before_decoding()
    {
        var refused = Assert.IsType<PictureResult.Refused>(await AddAsync(Store(maxPixels: 100), TestPictures.Png(20, 20)));

        Assert.Contains("megapixels", refused.Message);
        Assert.Empty(PictureFiles());
    }

    [Fact]
    public async Task Oversize_upload_leaves_nothing()
    {
        var store = Store();

        Assert.IsType<PictureResult.TooLarge>(await AddAsync(store, new byte[PictureStore.MaxUploadBytes + 1]));

        Assert.Empty(PartFiles());
        Assert.Empty(PictureFiles());
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Label_is_cleaned()
    {
        var store = Store();

        var odd = Assert.IsType<PictureResult.Added>(await AddAsync(store, TestPictures.Png(8, 8), "..\\..\\evil\u0007name.png"));
        var longOne = Assert.IsType<PictureResult.Added>(await AddAsync(store, TestPictures.Png(8, 8), new string('x', 300)));
        var none = Assert.IsType<PictureResult.Added>(await AddAsync(store, TestPictures.Png(8, 8), null));

        Assert.Equal("evilname.png", odd.Picture.Label);
        Assert.Equal(120, longOne.Picture.Label.Length);
        Assert.Equal("Picture", none.Picture.Label);
    }

    [Fact]
    public async Task Names_are_checked()
    {
        var store = Store();
        var added = Assert.IsType<PictureResult.Added>(await AddAsync(store, TestPictures.Png(8, 8)));

        Assert.Null(store.Open("../index.json"));
        Assert.Null(store.Open(new string('A', 32) + ".png"));
        Assert.Null(store.Open(added.Picture.Name + "\n"));
        Assert.Null(store.Open(new string('a', 32) + ".png"));
        Assert.False(store.Delete(new string('a', 32) + ".png"));
        Assert.True(store.Delete(added.Picture.Name));
        Assert.Null(store.Open(added.Picture.Name));
        Assert.Empty(PictureFiles());
    }

    [Fact]
    public async Task Index_survives_a_restart()
    {
        var added = Assert.IsType<PictureResult.Added>(await AddAsync(Store(), TestPictures.Png(8, 8), "beach.png"));

        var again = Store().List();

        var row = Assert.Single(again);
        Assert.Equal(added.Picture.Name, row.Name);
        Assert.Equal("beach.png", row.Label);
        Assert.Equal("Test Owner", row.AddedBy);
    }

    [Fact]
    public async Task MarkUse_sets_and_clears_unused_since()
    {
        var store = Store();
        var a = Assert.IsType<PictureResult.Added>(await AddAsync(store, TestPictures.Png(8, 8))).Picture.Name;
        var b = Assert.IsType<PictureResult.Added>(await AddAsync(store, TestPictures.Png(8, 8))).Picture.Name;

        store.MarkUse(new HashSet<string> { a });
        Assert.Null(store.Get(a)!.UnusedSince);
        Assert.Equal(_time.GetUtcNow(), store.Get(b)!.UnusedSince);

        var first = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromDays(1));
        store.MarkUse(new HashSet<string>());
        Assert.Equal(first, store.Get(b)!.UnusedSince); // the first day it was seen unused is kept

        store.MarkUse(new HashSet<string> { a, b });
        Assert.Null(store.Get(a)!.UnusedSince);
        Assert.Null(store.Get(b)!.UnusedSince);
    }
}
