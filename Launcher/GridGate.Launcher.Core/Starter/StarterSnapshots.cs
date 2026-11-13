using GridGate.Launcher.Core.Pictures;
using SkiaSharp;

namespace GridGate.Launcher.Core.Starter;

/// <summary>
/// A starter avatar's snapshot (spec 2026-11-11-starter-avatars-design §6.3): a PNG, JPEG or WebP of at most <see cref="MaxBytes"/>,
/// judged by its contents. Its size in pixels is checked from the header before anything is decoded. It is then redrawn upright,
/// scaled down to <see cref="MaxWidth"/> wide and saved as a new WebP, so nothing else in the file (EXIF, a location) is kept.
/// </summary>
public static class StarterSnapshots
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxWidth = 800;
    public const long MaxPixels = 40_000_000;

    public const string NotAPicture = "That file isn't a PNG, JPEG or WebP picture.";
    public const string TooBig = "That picture is over 5 MB.";
    public const string TooManyPixels = "That picture is too big to use (more than 40 megapixels).";
    public const string Damaged = "That picture couldn't be read; it may be damaged.";

    /// <summary>
    /// Saves <paramref name="input"/> as <c>&lt;id&gt;.webp</c> in <paramref name="folder"/>, replacing one already there. Returns
    /// the file name, or null with <paramref name="problem"/>.
    /// </summary>
    public static string? Save(Stream input, string folder, Guid id, out string? problem)
    {
        var bytes = ReadAtMost(input, MaxBytes);
        if (bytes is null)
        {
            problem = TooBig;
            return null;
        }
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
        {
            problem = NotAPicture;
            return null;
        }
        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            problem = TooManyPixels;
            return null;
        }

        var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, srgb);
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            problem = Damaged;
            return null;
        }

        var origin = codec.EncodedOrigin;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = swap ? (info.Height, info.Width) : (info.Width, info.Height);
        var scale = Math.Min(1.0, (double)MaxWidth / w);
        var (outW, outH) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));

        decoded.SetImmutable();
        using var output = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul, srgb));
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(decoded))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)outW / w, (float)outH / h);
            canvas.Concat(PictureStore.OrientationMatrix(origin, info.Width, info.Height));
            canvas.DrawImage(image, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        using var encodedImage = SKImage.FromBitmap(output);
        using var webp = encodedImage.Encode(SKEncodedImageFormat.Webp, 85);
        if (webp is null)
        {
            problem = Damaged;
            return null;
        }

        Directory.CreateDirectory(folder);
        var name = id + ".webp";
        var path = Path.Combine(folder, name);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, webp.ToArray());
        File.Move(tmp, path, overwrite: true);
        problem = null;
        return name;
    }

    /// <summary>The whole stream, or null when it has more than <paramref name="max"/> bytes (never reads more than one past it).</summary>
    private static byte[]? ReadAtMost(Stream input, long max)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = input.Read(chunk, 0, (int)Math.Min(chunk.Length, max + 1 - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > max) return null;
        }
        return buffer.ToArray();
    }
}
