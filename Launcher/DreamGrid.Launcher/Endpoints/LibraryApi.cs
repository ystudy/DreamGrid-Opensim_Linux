using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace DreamGrid.Launcher.Endpoints;

/// <summary>
/// The OAR library's upload and download (spec 5.5). An OAR can be many gigabytes, so the upload is streamed to disk and
/// never read as a form. Admin only; callers that aren't signed in get 401.
/// </summary>
public static class LibraryApi
{
    /// <summary>The refusal for an upload that isn't a gzip tar starting with archive.xml.</summary>
    public const string NotAnOar = "That file isn't an OAR.";

    private const string FileField = "oar";
    private const string TokenField = "__RequestVerificationToken";
    private const string TokenHeader = "RequestVerificationToken";
    private const int MaxTokenLength = 4096;

    // A full disk: ENOSPC on Linux (its errno is the HResult there); ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL on Windows.
    private const int Enospc = 28;
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    public static void Map(WebApplication app)
    {
        app.MapPost("/regions/library/upload", UploadAsync)
            .RequireAuthorization(AuthSchemes.AdminPolicy).WithMetadata(ApiEndpoint.Marker);

        app.MapGet("/regions/library/file/{name}", (string name, OarLibrary library) =>
                library.Resolve(name) is { } path
                    ? Results.File(path, "application/octet-stream", fileDownloadName: Path.GetFileName(path), enableRangeProcessing: true)
                    : Results.NotFound())
            .RequireAuthorization(AuthSchemes.AdminPolicy).WithMetadata(ApiEndpoint.Marker);
    }

    /// <summary>
    /// Streams the <c>oar</c> section into <c>.incoming</c>, checks it and moves it into the library under a free, safe
    /// name. With <c>X-Requested-With: fetch</c> the answer is JSON <c>{ ok, name, text }</c>; otherwise a redirect to the
    /// library page with the text as its notice.
    /// </summary>
    private static async Task<IResult> UploadAsync(HttpContext ctx, OarLibrary library, LauncherPaths paths,
        IAntiforgery antiforgery, ITempDataDictionaryFactory tempData, UploadPartFiles partFiles, ILoggerFactory loggers)
    {
        // No size limit for this endpoint only (Kestrel's default is 30 MB); it must be lifted before the body is read.
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = null;
        var fetch = ctx.Request.Headers["X-Requested-With"] == "fetch";

        IResult Reply(bool ok, string? name, string text)
        {
            // A successful fetch upload reloads the page, which then shows the notice too.
            if (ok || !fetch)
            {
                var notice = tempData.GetTempData(ctx);
                notice["Notice"] = text;
                notice.Save();
            }
            return fetch
                ? Results.Json(new { ok, name, text }, statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest)
                : Results.Redirect("/regions/library");
        }

        if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var type)
            || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary) is not { Length: > 0 } boundary)
            return Results.BadRequest();

        var ct = ctx.RequestAborted;
        var reader = new MultipartReader(boundary.ToString(), ctx.Request.Body);
        try
        {
            for (var section = await reader.ReadNextSectionAsync(ct); section is not null; section = await reader.ReadNextSectionAsync(ct))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
                    continue;
                var field = HeaderUtilities.RemoveQuotes(disposition.Name).ToString();

                // The form without JavaScript sends the token as its first field. Copied into the header, the antiforgery
                // check reads it from there and never reads (and so buffers) the form.
                if (field == TokenField && !disposition.IsFileDisposition())
                {
                    if (await ReadTokenAsync(section.Body, ct) is not { } token) return Results.BadRequest();
                    if (string.IsNullOrEmpty(ctx.Request.Headers[TokenHeader])) ctx.Request.Headers[TokenHeader] = token;
                    continue;
                }
                if (field != FileField) continue;

                // Nothing of the file is kept until the request is known to come from the library page.
                if (string.IsNullOrEmpty(ctx.Request.Headers[TokenHeader])) return Results.BadRequest();
                try
                {
                    await antiforgery.ValidateRequestAsync(ctx);
                }
                catch (AntiforgeryValidationException)
                {
                    return Results.BadRequest();
                }

                var uploaded = (disposition.FileNameStar.HasValue ? disposition.FileNameStar : HeaderUtilities.RemoveQuotes(disposition.FileName)).ToString();
                if (uploaded.Length == 0) break;
                var saved = await SaveAsync(section.Body, uploaded, library, paths, partFiles, ct);
                return saved is null ? Reply(false, null, NotAnOar) : Reply(true, saved, $"Uploaded {saved}.");
            }
            return Reply(false, null, "Choose an OAR file to upload.");
        }
        catch (BadHttpRequestException e)
        {
            return Results.StatusCode(e.StatusCode);
        }
        catch (PartWriteException e)
        {
            // SaveAsync has removed the part file.
            var cause = e.InnerException!;
            loggers.CreateLogger(typeof(LibraryApi).FullName!).LogWarning(cause, "An upload could not be written to {Folder}.", paths.OarsIncoming);
            return Reply(false, null, cause.HResult is Enospc or DiskFull or HandleDiskFull
                ? "There isn't enough disk space for that file."
                : "The file could not be saved; see the launcher log.");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // The browser went away; SaveAsync has removed the part file.
            return Reply(false, null, "The upload stopped before it finished. Try again.");
        }
    }

    /// <summary>A write to the upload's part file failed; the inner exception says why.</summary>
    private sealed class PartWriteException(Exception inner) : Exception(inner.Message, inner);

    private static async Task WritingAsync(Func<Task> write)
    {
        try { await write(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new PartWriteException(e); }
    }

    /// <summary>The token field's value, or null when it is longer than any token.</summary>
    private static async Task<string?> ReadTokenAsync(Stream body, CancellationToken ct)
    {
        using var text = new StreamReader(body);
        var buffer = new char[MaxTokenLength + 1];
        var length = 0;
        for (int n; length < buffer.Length && (n = await text.ReadAsync(buffer.AsMemory(length), ct)) > 0;)
            length += n;
        return length > MaxTokenLength ? null : new string(buffer, 0, length);
    }

    /// <summary>The library name the upload was saved as, or null (and nothing kept) when it isn't an OAR.</summary>
    /// <remarks>
    /// The body is copied by hand so that a failed write to the part file (a <see cref="PartWriteException"/>) is told apart
    /// from a failed read of the body, which is the browser going away.
    /// </remarks>
    private static async Task<string?> SaveAsync(Stream body, string uploaded, OarLibrary library, LauncherPaths paths,
        UploadPartFiles partFiles, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.OarsIncoming);
        var part = Path.Combine(paths.OarsIncoming, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            Stream file;
            try { file = partFiles.Create(part); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new PartWriteException(e); }
            try
            {
                var buffer = new byte[81920];
                for (int n; (n = await body.ReadAsync(buffer, ct)) > 0;)
                    await WritingAsync(() => file.WriteAsync(buffer.AsMemory(0, n), ct).AsTask());
                await WritingAsync(() => file.FlushAsync(ct));
            }
            finally
            {
                // After a failed write, closing tries the buffered bytes again; that failure must not hide the first one.
                try { await file.DisposeAsync(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (!OarFile.LooksLikeOar(part)) return null;

            var clean = OarLibrary.CleanName(uploaded);
            for (var attempt = 1; ; attempt++)
            {
                var name = library.FreeName(clean);
                var target = Path.Combine(paths.Oars, name);
                try
                {
                    File.Move(part, target);
                    return name;
                }
                catch (IOException) when (attempt < 10 && File.Exists(target))
                {
                    // Another upload took the name meanwhile; FreeName finds the next one.
                }
            }
        }
        finally
        {
            try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
