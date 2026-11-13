using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Iar;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Antiforgery;

namespace GridGate.Launcher.Endpoints;

/// <summary>
/// The owner's archive upload for starter avatars (spec 2026-11-11-starter-avatars-design §6.2): no size limit, streamed to
/// <see cref="LauncherPaths.IarsIncoming"/> like the staff IAR upload, then back to the page to pick the avatars. OwnerPolicy;
/// 404 where there is no guest side.
/// </summary>
public static class StarterApi
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/security/starter-avatars/upload", UploadAsync).RequireAuthorization(AuthSchemes.OwnerPolicy).WithMetadata(ApiEndpoint.Marker);
    }

    private static async Task<IResult> UploadAsync(HttpContext ctx, IAntiforgery antiforgery, LauncherPaths paths, Func<LauncherSettings> settings)
    {
        if (!AdminRule.HasStaffPages(settings())) return Results.NotFound();
        var (path, problem) = await IarUpload.ReceiveAsync(ctx, antiforgery, paths, maxBytes: null);
        if (problem == IarUploadProblem.BadRequest) return Results.BadRequest();
        return path is null
            ? Results.Redirect($"/security/starter-avatars?problem={problem}")
            : Results.Redirect($"/security/starter-avatars?pending={Path.GetFileNameWithoutExtension(path)}");
    }
}
