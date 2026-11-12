using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Starter;

namespace DreamGrid.Launcher.Endpoints;

/// <summary>
/// A ready starter avatar's snapshot for the sign-up page (spec 2026-11-11-starter-avatars-design §6.6), anonymous, at
/// <c>/starter-avatars/&lt;id&gt;.webp</c>: the path says nothing about the model account. Anything not ready is 404.
/// </summary>
public static class StarterSnapshotApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/starter-avatars/{id:guid}.webp", (Guid id, HttpContext ctx, StarterAvatarList starters, LauncherPaths paths) =>
        {
            if (starters.Ready().FirstOrDefault(a => a.Id == id) is not { Snapshot: { } file }) return Results.NotFound();
            var path = Path.Combine(paths.StarterAvatars, Path.GetFileName(file));
            if (!File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            return Results.File(path, "image/webp");
        }).AllowAnonymous();
    }
}
