using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Iar;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Antiforgery;

namespace DreamGrid.Launcher.Endpoints;

/// <summary>
/// The resident's IAR upload and download (spec 2026-11-11-iar-backups-design §5): GuestPolicy, and 404 unless
/// <see cref="IarPagesOnlyAttribute"/> shows the page. The upload is refused before a byte is kept when the rules say no, and
/// is cut off at the resident's size limit while it streams. A download link answers 404, never 403, unless it is this resident's,
/// in this session, and not expired; it is sent at the resident's speed limit.
/// </summary>
public static class IarGuestApi
{
    private const long Mb = 1L << 20;

    public static void Map(WebApplication app)
    {
        app.MapPost("/guest/inventory/upload", UploadAsync).RequireAuthorization(AuthSchemes.GuestPolicy).WithMetadata(ApiEndpoint.Marker);
        app.MapGet("/guest/inventory/file/{token}", Download).RequireAuthorization(AuthSchemes.GuestPolicy).WithMetadata(ApiEndpoint.Marker);
    }

    private static async Task<IResult> UploadAsync(HttpContext ctx, IAntiforgery antiforgery, LauncherPaths paths, Func<LauncherSettings> settings,
        IarRights rights, IarJobs jobs)
    {
        var s = settings();
        if (!IarPagesOnlyAttribute.Shown(s, rights, ctx.User) || GuestSignIn.Read(ctx.User) is not { } guest) return Results.NotFound();
        if (jobs.Check(guest.Principal, IarJobKind.Load) is { } early) return Results.Redirect($"/guest/inventory?refused={early}");
        var limit = IarRights.Effective(s.Iar, rights.For(guest.Principal)).MaxUploadMb * Mb;
        var (path, problem) = await IarUpload.ReceiveAsync(ctx, antiforgery, paths, limit);
        if (problem == IarUploadProblem.BadRequest) return Results.BadRequest();
        if (path is null) return Results.Redirect($"/guest/inventory?problem={problem}");
        var parts = (ctx.User.Identity?.Name ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var refusal = jobs.TryStartUpload(guest.Principal, parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "", path,
            ctx.Connection.RemoteIpAddress?.ToString());
        return Results.Redirect(refusal is null ? "/guest/inventory" : $"/guest/inventory?refused={refusal}");
    }

    private static IResult Download(string token, HttpContext ctx, IarFiles files, IarRights rights, Func<LauncherSettings> settings, AuditLog audit,
        TimeProvider time)
    {
        var s = settings();
        if (!IarPagesOnlyAttribute.Shown(s, rights, ctx.User) || GuestSignIn.Read(ctx.User) is not { } guest) return Results.NotFound();
        if (files.ForDownload(token, guest.Principal, guest.Stamp) is not { } ready) return Results.NotFound();
        var name = LogText.Clean(ctx.User.Identity?.Name, 64);
        audit.Write(new AuditEntry(name, guest.Principal, AuthSchemes.GuestRole, ctx.Connection.RemoteIpAddress?.ToString(), "iar-download-fetched", name, "ok"));
        var rate = IarRights.Effective(s.Iar, rights.For(guest.Principal)).DownloadKbPerSecond * 1024;
        var stream = new ThrottledStream(File.OpenRead(files.PathOf(ready)), rate, time);
        return Results.File(stream, "application/octet-stream", $"inventory-{time.GetUtcNow().UtcDateTime:yyyy-MM-dd}.iar", enableRangeProcessing: true);
    }
}
