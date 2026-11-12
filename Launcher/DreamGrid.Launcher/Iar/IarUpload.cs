using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Iar;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace DreamGrid.Launcher.Iar;

/// <summary>Why an IAR upload wasn't kept.</summary>
public enum IarUploadProblem { BadRequest, NoFile, TooBig, NotAnIar, Stopped, NoSpace }

/// <summary>
/// Receives one IAR upload, streamed (never form binding), into <see cref="LauncherPaths.IarsIncoming"/> under a random name, the
/// way the OAR library does: the antiforgery token comes first as a form field and is checked before any byte is kept; the size
/// cap, when there is one, is enforced while the bytes stream in, so an oversized file is cut off and removed, never stored.
/// </summary>
public static class IarUpload
{
    public const string FileField = "iar";
    private const string TokenField = "__RequestVerificationToken";
    private const string TokenHeader = "RequestVerificationToken";
    private const int MaxTokenLength = 4096;

    /// <summary>The full path of the kept upload, or why there is none (and nothing was kept).</summary>
    public static async Task<(string? Path, IarUploadProblem? Problem)> ReceiveAsync(HttpContext ctx, IAntiforgery antiforgery, LauncherPaths paths,
        long? maxBytes)
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maxBytes is { } m ? m + (1 << 20) : null;
        if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var type)
            || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary) is not { Length: > 0 } boundary)
            return (null, IarUploadProblem.BadRequest);

        var ct = ctx.RequestAborted;
        var reader = new MultipartReader(boundary.ToString(), ctx.Request.Body);
        string? part = null;
        try
        {
            for (var section = await reader.ReadNextSectionAsync(ct); section is not null; section = await reader.ReadNextSectionAsync(ct))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) continue;
                var field = HeaderUtilities.RemoveQuotes(disposition.Name).ToString();
                if (field == TokenField && !disposition.IsFileDisposition())
                {
                    using var text = new StreamReader(section.Body);
                    var buffer = new char[MaxTokenLength + 1];
                    var length = await text.ReadBlockAsync(buffer, ct);
                    if (length > MaxTokenLength) return (null, IarUploadProblem.BadRequest);
                    if (string.IsNullOrEmpty(ctx.Request.Headers[TokenHeader])) ctx.Request.Headers[TokenHeader] = new string(buffer, 0, length);
                    continue;
                }
                if (field != FileField || !disposition.IsFileDisposition()) continue;
                if (string.IsNullOrEmpty(ctx.Request.Headers[TokenHeader])) return (null, IarUploadProblem.BadRequest);
                try { await antiforgery.ValidateRequestAsync(ctx); }
                catch (AntiforgeryValidationException) { return (null, IarUploadProblem.BadRequest); }

                Directory.CreateDirectory(paths.IarsIncoming);
                part = Path.Combine(paths.IarsIncoming, Guid.NewGuid().ToString("N") + ".part");
                long written = 0;
                await using (var output = File.Create(part))
                {
                    var chunk = new byte[81920];
                    for (int n; (n = await section.Body.ReadAsync(chunk, ct)) > 0;)
                    {
                        written += n;
                        if (maxBytes is { } max && written > max) return (null, IarUploadProblem.TooBig);
                        await output.WriteAsync(chunk.AsMemory(0, n), ct);
                    }
                }
                if (written == 0) return (null, IarUploadProblem.NoFile);
                if (!IarArchive.LooksLikeIar(part)) return (null, IarUploadProblem.NotAnIar);
                var kept = Path.ChangeExtension(part, ".iar");
                File.Move(part, kept);
                part = null;
                return (kept, null);
            }
            return (null, IarUploadProblem.NoFile);
        }
        catch (BadHttpRequestException)
        {
            return (null, IarUploadProblem.TooBig);
        }
        catch (IOException e) when (e.HResult is 28 or unchecked((int)0x80070070) or unchecked((int)0x80070027))
        {
            return (null, IarUploadProblem.NoSpace);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            return (null, IarUploadProblem.Stopped);
        }
        finally
        {
            if (part is not null)
            {
                try { File.Delete(part); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
