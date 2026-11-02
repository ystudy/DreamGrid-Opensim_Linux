using System.Text;
using SkiaSharp;

namespace DreamGrid.Launcher.Tests;

/// <summary>Small pictures made on the spot for the picture tests: a colour gradient, so each encoding has real content.</summary>
internal static class TestPictures
{
    public static byte[] Png(int w, int h) => Encode(w, h, SKEncodedImageFormat.Png);

    public static byte[] Jpeg(int w, int h) => Encode(w, h, SKEncodedImageFormat.Jpeg);

    public static byte[] Webp(int w, int h) => Encode(w, h, SKEncodedImageFormat.Webp);

    /// <summary>The smallest GIF: a 1 × 1 GIF89a.</summary>
    public static byte[] Gif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
        0x00, 0x00, 0x00, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
    ];

    /// <summary>A JPEG with an Exif segment right after the start marker, holding a made-up GPS line.</summary>
    public static byte[] JpegWithExif(int w, int h)
    {
        var jpeg = Jpeg(w, h);
        var payload = Encoding.ASCII.GetBytes("Exif\0\0GPSLatitude 51.5");
        var length = payload.Length + 2;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    /// <summary>A PNG with a text chunk after its header, holding made-up text (the CRC is left wrong; decoders skip it).</summary>
    public static byte[] PngWithText(int w, int h)
    {
        var png = Png(w, h);
        var text = Encoding.ASCII.GetBytes("Comment\0secret note");
        byte[] chunk = [0, 0, 0, (byte)text.Length, .. "tEXt"u8, .. text, 0, 0, 0, 0];
        // The 8-byte signature, then the 25-byte IHDR chunk.
        return [.. png[..33], .. chunk, .. png[33..]];
    }

    private static byte[] Encode(int w, int h, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h),
                [SKColors.Red, SKColors.Blue], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, w, h, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
