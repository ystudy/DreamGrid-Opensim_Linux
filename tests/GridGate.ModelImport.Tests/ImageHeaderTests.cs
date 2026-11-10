using GridGate.ModelImport.Reading;
using Xunit;

namespace GridGate.ModelImport.Tests;

public class ImageHeaderTests
{
    // SOI, an APP0 segment, then the given frame marker: length 11, precision 8, height, width, one component.
    private static byte[] Jpeg(byte sof, int width, int height, bool dhtFirst = false)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46 };
        if (dhtFirst) b.AddRange(new byte[] { 0xFF, 0xC4, 0x00, 0x03, 0x00 });
        b.AddRange(new byte[] { 0xFF, sof, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0 });
        return b.ToArray();
    }

    [Fact]
    public void Png_size_comes_from_IHDR()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0x27, 0x10, 0, 0, 0, 10, 8, 2 };
        Assert.Equal((10000, 10), ImageHeader.Size(png));
    }

    [Fact]
    public void Png_whose_first_chunk_is_not_IHDR_has_no_size()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x74, 0x45, 0x58, 0x74, 0, 0, 0x27, 0x10, 0, 0, 0, 10 };
        Assert.Null(ImageHeader.Size(png));
    }

    [Theory]
    [InlineData((byte)0xC0)] // baseline
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)] // progressive
    [InlineData((byte)0xCF)]
    public void Jpeg_size_comes_from_the_frame_marker(byte sof)
    {
        Assert.Equal((9000, 300), ImageHeader.Size(Jpeg(sof, 9000, 300)));
    }

    [Fact]
    public void Jpeg_skips_a_table_segment_that_looks_like_a_frame_marker()
    {
        Assert.Equal((640, 480), ImageHeader.Size(Jpeg(0xC0, 640, 480, dhtFirst: true)));
    }

    [Theory]
    [InlineData((byte)0xC4)] // DHT
    [InlineData((byte)0xC8)] // JPG
    [InlineData((byte)0xCC)] // DAC
    public void Segments_that_are_not_frames_give_no_size(byte marker)
    {
        Assert.Null(ImageHeader.Size(Jpeg(marker, 100, 100)));
    }

    [Fact]
    public void Other_or_cut_short_data_has_no_size()
    {
        Assert.Null(ImageHeader.Size(new byte[] { 1, 2, 3 }));
        Assert.Null(ImageHeader.Size(ReadOnlySpan<byte>.Empty));
        Assert.Null(ImageHeader.Size(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }));
        Assert.Null(ImageHeader.Size(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00 }));
    }
}
