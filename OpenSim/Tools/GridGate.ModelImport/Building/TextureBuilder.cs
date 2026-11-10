using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using GridGate.ModelImport.Model;
using GridGate.ModelImport.Reading;
using OpenMetaverse.Imaging;

namespace GridGate.ModelImport.Building;

/// <summary>A texture ready for upload: JPEG2000 bytes (empty when the image could not be used), its size, and what was done to it.</summary>
public sealed record TextureResult(byte[] J2k, int Width, int Height, Finding? Finding);

/// <summary>
/// Turns a glTF image into a grid texture: decoded with System.Drawing (the same library OpenSim uses), each side rounded to the
/// nearest power of two and kept within maxSide, then encoded as JPEG2000 by OpenSim's OpenJPEG wrapper.
/// </summary>
public static class TextureBuilder
{
    // OpenJPEG is a native library that OpenMetaverse finds through OpenMetaverse.dll.config; dotnet ignores that map, so OpenSim's
    // own fallback is installed before the first encode (a second installation after Main's is harmless).
    static TextureBuilder() => OpenSim.Framework.Util.InstallNativeDllFallback();

    public static TextureResult Build(ImportImage image, int maxSide)
    {
        // Decided by the header, not by the declared type; only PNG and JPEG are ever handed to the decoder.
        var size = ImageHeader.Size(image.Bytes);
        if (size == null || ImageHeader.Format(image.Bytes) is not ("png" or "jpeg"))
            return Unsupported(image);

        try
        {
            using var stream = new MemoryStream(image.Bytes, writable: false);
            using var source = new Bitmap(stream);

            int fromW = source.Width, fromH = source.Height;
            int w = Math.Min(NearestPowerOfTwo(fromW), maxSide);
            int h = Math.Min(NearestPowerOfTwo(fromH), maxSide);

            bool alpha = Image.IsAlphaPixelFormat(source.PixelFormat) || (source.Flags & (int)ImageFlags.HasAlpha) != 0;
            using var resized = new Bitmap(w, h, alpha ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, w, h), 0, 0, fromW, fromH, GraphicsUnit.Pixel, WrapClamp());
            }

            var j2k = OpenJPEG.EncodeFromImage(resized, false);
            if (j2k == null || j2k.Length == 0) return Unsupported(image);

            Finding? finding = null;
            int largest = Math.Max(fromW, fromH);
            if (largest > maxSide)
                finding = Make("texture-shrunk", FindingKind.Risky, image.Name, ("from", Inv(largest)), ("to", Inv(Math.Max(w, h))));
            else if (w != fromW || h != fromH)
                finding = Make("texture-resized", FindingKind.Safe, image.Name);
            return new TextureResult(j2k, w, h, finding);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException or InvalidOperationException)
        {
            // A damaged image that passed the header check.
            return Unsupported(image);
        }
    }

    /// <summary>The nearer of the two powers of two around the value; halfway goes up.</summary>
    internal static int NearestPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        int lower = 1;
        while (lower <= value / 2) lower <<= 1;
        if (lower == value) return lower;
        int upper = lower << 1;
        return value - lower < upper - value ? lower : upper;
    }

    private static ImageAttributes WrapClamp()
    {
        var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        return attributes;
    }

    private static TextureResult Unsupported(ImportImage image) =>
        new(Array.Empty<byte>(), 0, 0, Make("texture-unsupported", FindingKind.Risky, image.Name, ("format", ImageHeader.Format(image.Bytes))));

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static Finding Make(string code, FindingKind kind, string imageName, params (string Key, string Value)[] values)
    {
        var d = new Dictionary<string, string> { ["image"] = imageName };
        foreach (var (k, v) in values) d[k] = v;
        return new Finding(code, kind, d);
    }
}
