using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Iar;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Antiforgery;

namespace DreamGrid.Launcher.Endpoints;

/// <summary>
/// The owner's and admins' IAR upload (no size limit, spec 2026-11-11-iar-backups-design §7.2) and the download of a kept staff save,
/// under <c>/people/{id}/iar</c>. AdminPolicy: supervisors get 403. Where there are no staff pages, 404.
/// </summary>
public static partial class IarStaffApi
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/people/{id:guid}/iar/upload", UploadAsync).RequireAuthorization(AuthSchemes.AdminPolicy).WithMetadata(ApiEndpoint.Marker);
        app.MapGet("/people/{id:guid}/iar/file/{name}", Download).RequireAuthorization(AuthSchemes.AdminPolicy).WithMetadata(ApiEndpoint.Marker);
    }

    private static async Task<IResult> UploadAsync(Guid id, HttpContext ctx, IAntiforgery antiforgery, LauncherPaths paths, Func<LauncherSettings> settings)
    {
        if (!AdminRule.HasStaffPages(settings())) return Results.NotFound();
        var (path, problem) = await IarUpload.ReceiveAsync(ctx, antiforgery, paths, maxBytes: null);
        if (problem == IarUploadProblem.BadRequest) return Results.BadRequest();
        return path is null
            ? Results.Redirect($"/people/{id}/iar?problem={problem}")
            : Results.Redirect($"/people/{id}/iar?pending={Path.GetFileNameWithoutExtension(path)}");
    }

    private static IResult Download(Guid id, string name, HttpContext ctx, IarStaffRecords records, IarStaffJobs jobs, LauncherPaths paths,
        Func<LauncherSettings> settings)
    {
        if (!AdminRule.HasStaffPages(settings())) return Results.NotFound();
        if (!FileName().IsMatch(name) || records.KeptFiles(id).FirstOrDefault(r => r.File == name + ".iar") is not { } kept) return Results.NotFound();
        var (actor, actorId, role) = AuditActor.Of(ctx.User);
        jobs.Fetched(new IarStaffActor(actor, actorId ?? Guid.Empty, role, ctx.Connection.RemoteIpAddress?.ToString()), kept);
        return Results.File(Path.Combine(paths.IarsStaff, kept.File!), "application/octet-stream",
            fileDownloadName: $"evidence-{kept.At.UtcDateTime:yyyy-MM-dd}-{name[..8]}.iar", enableRangeProcessing: true);
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    internal static partial Regex FileName();
}
