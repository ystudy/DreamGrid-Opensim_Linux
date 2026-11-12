using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
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
        var parts = (ctx.User.Identity?.Name ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var (first, last) = (parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "");
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        // The slot and the day's count are taken before any byte is kept, so parallel uploads are refused, not stored.
        if (jobs.BeginUpload(guest.Principal, first, last, ip) is { } early) return Results.Redirect($"/guest/inventory?refused={early}");
        var limit = IarRights.Effective(s.Iar, rights.For(guest.Principal)).MaxUploadMb * Mb;
        var (path, problem) = await IarUpload.ReceiveAsync(ctx, antiforgery, paths, limit);
        if (path is null)
        {
            jobs.AbandonUpload(guest.Principal, $"{first} {last}", problem!.Value, ip);
            return problem == IarUploadProblem.BadRequest ? Results.BadRequest() : Results.Redirect($"/guest/inventory?problem={problem}");
        }
        jobs.ContinueUpload(guest.Principal, first, last, path, ip);
        return Results.Redirect("/guest/inventory");
    }

    private static IResult Download(string token, HttpContext ctx, IarFiles files, IarRights rights, Func<LauncherSettings> settings, AuditLog audit,
        TimeProvider time, TakedownStore takedowns, IarDownloadGate gate)
    {
        var s = settings();
        if (!IarPagesOnlyAttribute.Shown(s, rights, ctx.User) || GuestSignIn.Read(ctx.User) is not { } guest) return Results.NotFound();
        // Named in an open case after the backup was made: the link stops working too (final review, Important 6).
        if (takedowns.NamedInOpenCase(guest.Principal)) return Results.NotFound();
        if (files.ForDownload(token, guest.Principal, guest.Stamp) is not { } ready) return Results.NotFound();
        if (gate.TryEnter(guest.Principal) is not { } ticket) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        var name = LogText.Clean(ctx.User.Identity?.Name, 64);
        audit.Write(new AuditEntry(name, guest.Principal, AuthSchemes.GuestRole, ctx.Connection.RemoteIpAddress?.ToString(), "iar-download-fetched", name, "ok"));
        var rate = IarRights.Effective(s.Iar, rights.For(guest.Principal)).DownloadKbPerSecond * 1024L;
        var stream = new ThrottledStream(File.OpenRead(files.PathOf(ready)), rate, time, ticket.Dispose);
        return Results.File(stream, "application/octet-stream", $"inventory-{time.GetUtcNow().UtcDateTime:yyyy-MM-dd}.iar", enableRangeProcessing: true);
    }
}
