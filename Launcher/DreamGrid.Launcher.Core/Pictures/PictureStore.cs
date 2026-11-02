using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace DreamGrid.Launcher.Core.Pictures;

/// <param name="Name">The launcher's own name for the file (<see cref="PictureStore.IsName"/>).</param>
/// <param name="Label">The uploader's file name, cleaned: shown in lists, never used as a path.</param>
/// <param name="UnusedSince">When a clean-up pass first found nothing using it; null while something does.</param>
public sealed record StoredPicture(string Name, string Label, int Width, int Height, long Bytes,
    DateTimeOffset Added, string AddedBy, DateTimeOffset? UnusedSince);

public abstract record PictureResult
{
    public sealed record Added(StoredPicture Picture) : PictureResult;
    /// <param name="Message">One plain sentence for the person who uploaded it.</param>
    public sealed record Refused(string Message) : PictureResult;
    public sealed record TooLarge : PictureResult;
}

/// <summary>
/// One folder of pictures people uploaded, plus <c>index.json</c> beside them (spec 2026-11-01-viewer-splash-design §4.4).
/// An upload is streamed to <c>.incoming</c> and judged by its contents alone: only PNG, JPEG and WebP within
/// <see cref="MaxUploadBytes"/> and <see cref="MaxPixels"/> are kept. Each kept picture is redrawn (its EXIF orientation
/// applied, then EXIF and anything else hidden in the file dropped, scaled down to <see cref="MaxSide"/>) and saved under a
/// random name, so nothing the uploader chose reaches a path or a page as-is. Safe to call from any thread.
/// </summary>
public sealed partial class PictureStore
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    public const int MaxSide = 3840;
    public const int MaxLabel = 120;

    public const string NotAPicture = "That file isn't a PNG, JPEG or WebP picture.";
    public const string Damaged = "That picture couldn't be read; it may be damaged.";
    public const string TooManyPixels = "That picture is too big to use (more than 50 megapixels).";

    private sealed record PictureIndex(List<StoredPicture> Pictures);

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly JsonStateFile<PictureIndex> _index;
    private readonly Lock _gate = new();

    public PictureStore(string folder, TimeProvider time, ILogger? log = null)
    {
        _folder = folder;
        _time = time;
        _index = new(Path.Combine(folder, "index.json"), () => new([]), log);
    }

    /// <summary>Checked before decoding, so a small file that claims a huge picture is never drawn.</summary>
    public long MaxPixels { get; init; } = 50_000_000;

    [GeneratedRegex("^[0-9a-f]{32}\\.(png|jpg|webp)\\z")]
    private static partial Regex NamePattern();

    public static bool IsName(string? name) => name is not null && NamePattern().IsMatch(name);

    public async Task<PictureResult> AddAsync(Stream source, string? label, string addedBy, CancellationToken ct)
    {
        var incoming = Path.Combine(_folder, ".incoming");
        Directory.CreateDirectory(incoming);
        var part = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                for (int n; (n = await source.ReadAsync(buffer, ct)) > 0;)
                {
                    total += n;
                    if (total > MaxUploadBytes) return new PictureResult.TooLarge();
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            return Redraw(part) switch
            {
                (null, var message) => new PictureResult.Refused(message!),
                ({ } drawn, _) => new PictureResult.Added(Keep(drawn, label, addedBy)),
            };
        }
        finally
        {
            try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public bool Exists(string name) => Open(name) is not null;

    public StoredPicture? Get(string name) => IsName(name) ? _index.Load().Pictures.FirstOrDefault(p => p.Name == name) : null;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<StoredPicture> List() => _index.Load().Pictures.OrderByDescending(p => p.Added).ToList();

    /// <summary>The file and its content type; null for a name that isn't well formed, isn't listed or whose file is gone.</summary>
    public (string Path, string ContentType)? Open(string name)
    {
        if (Get(name) is null) return null;
        var path = Path.Combine(_folder, name);
        return File.Exists(path) ? (path, ContentTypeOf(name)) : null;
    }

    /// <summary>False when the name isn't listed.</summary>
    public bool Delete(string name)
    {
        lock (_gate)
        {
            var index = _index.Load();
            if (!IsName(name) || index.Pictures.RemoveAll(p => p.Name == name) == 0) return false;
            _index.Save(index);
            try { File.Delete(Path.Combine(_folder, name)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return true;
        }
    }

    /// <summary>
    /// Records what is in use now: a used picture's <see cref="StoredPicture.UnusedSince"/> is cleared, and an unused one
    /// keeps the first time it was found unused (or gets now).
    /// </summary>
    public void MarkUse(IReadOnlySet<string> inUse)
    {
        lock (_gate)
        {
            var index = _index.Load();
            var now = _time.GetUtcNow();
            var changed = false;
            for (var i = 0; i < index.Pictures.Count; i++)
            {
                var p = index.Pictures[i];
                DateTimeOffset? next = inUse.Contains(p.Name) ? null : p.UnusedSince ?? now;
                if (next == p.UnusedSince) continue;
                index.Pictures[i] = p with { UnusedSince = next };
                changed = true;
            }
            if (changed) _index.Save(index);
        }
    }

    private sealed record Drawn(byte[] Bytes, string Extension, int Width, int Height);

    /// <summary>The picture redrawn, or null and the reason it was refused.</summary>
    private (Drawn?, string?) Redraw(string part)
    {
        using var codec = SKCodec.Create(part, out _);
        if (codec is null) return (null, NotAPicture);
        var (format, extension) = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => (SKEncodedImageFormat.Png, "png"),
            SKEncodedImageFormat.Jpeg => (SKEncodedImageFormat.Jpeg, "jpg"),
            SKEncodedImageFormat.Webp => (SKEncodedImageFormat.Webp, "webp"),
            _ => (default(SKEncodedImageFormat?), ""),
        };
        if (format is null) return (null, NotAPicture);
        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels) return (null, TooManyPixels);

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        // A file cut short decodes as "incomplete input" with part of the picture missing: refused, not kept half-drawn.
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success) return (null, Damaged);

        var origin = codec.EncodedOrigin;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = swap ? (info.Height, info.Width) : (info.Width, info.Height);
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(w, h));
        var (outW, outH) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));

        using var output = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(decoded))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)outW / w, (float)outH / h);
            canvas.Concat(OrientationMatrix(origin, info.Width, info.Height));
            canvas.DrawImage(image, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        using var encodedImage = SKImage.FromBitmap(output);
        using var data = encodedImage.Encode(format.Value, 90);
        return data is null ? (null, Damaged) : (new Drawn(data.ToArray(), extension, outW, outH), null);
    }

    /// <summary>Maps the stored pixels (width × height) onto the upright picture, for each EXIF orientation.</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private StoredPicture Keep(Drawn drawn, string? label, string addedBy)
    {
        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "." + drawn.Extension;
        var path = Path.Combine(_folder, name);
        // Written beside it and moved into place, so a reader never sees half a picture.
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, drawn.Bytes);
        File.Move(tmp, path);
        var picture = new StoredPicture(name, CleanLabel(label), drawn.Width, drawn.Height, drawn.Bytes.LongLength,
            _time.GetUtcNow(), addedBy, null);
        lock (_gate)
        {
            var index = _index.Load();
            index.Pictures.Add(picture);
            if (!_index.Save(index))
            {
                try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw new IOException("The picture list could not be saved.");
            }
        }
        return picture;
    }

    /// <summary>The file name only (no folders, either kind of slash), without control characters, at most <see cref="MaxLabel"/> long.</summary>
    private static string CleanLabel(string? label)
    {
        var name = (label ?? "").Split('/', '\\').Last();
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxLabel) clean = clean[..MaxLabel];
        return clean.Length == 0 ? "Picture" : clean;
    }

    private static string ContentTypeOf(string name) => Path.GetExtension(name) switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        _ => "image/webp",
    };
}
