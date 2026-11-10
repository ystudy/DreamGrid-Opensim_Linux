using System;

namespace GridGate.ModelImport.Reading;

/// <summary>Reads an image's pixel size from its header alone, so an oversized image is refused before anything decodes it.</summary>
public static class ImageHeader
{
    private static readonly byte[] PngSignature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>The width and height of a PNG (from IHDR) or JPEG (from the first SOF marker); null for anything else or a cut-short header.</summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature)) return Png(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) return Jpeg(bytes);
        return null;
    }

    // 8-byte signature, then the first chunk, which must be IHDR: length (4), "IHDR" (4), width (4), height (4), big-endian.
    private static (int, int)? Png(ReadOnlySpan<byte> b)
    {
        if (b.Length < 24) return null;
        if (b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return null;
        var w = ReadUInt32(b, 16);
        var h = ReadUInt32(b, 20);
        return (Clamp(w), Clamp(h));
    }

    // Walks the marker segments until a start-of-frame marker: SOF0-SOF15 except SOF4 (DHT), SOF8 (JPG) and SOF12 (DAC).
    // A SOF segment is length (2), precision (1), height (2), width (2), big-endian.
    private static (int, int)? Jpeg(ReadOnlySpan<byte> b)
    {
        int i = 2;
        while (i + 1 < b.Length)
        {
            if (b[i] != 0xFF) return null; // not at a marker
            int marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; } // fill byte
            i += 2;
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8)) continue; // no length: TEM, RSTn, SOI
            if (marker == 0xD9 || marker == 0xDA) return null; // end of image, or start of scan: no frame header came first
            if (i + 2 > b.Length) return null;
            int length = (b[i] << 8) | b[i + 1];
            if (length < 2) return null;
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (length < 7 || i + 7 > b.Length) return null;
                int h = (b[i + 3] << 8) | b[i + 4];
                int w = (b[i + 5] << 8) | b[i + 6];
                return (w, h);
            }
            i += length;
        }
        return null;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> b, int at) =>
        ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

    private static int Clamp(uint v) => v > int.MaxValue ? int.MaxValue : (int)v;
}
