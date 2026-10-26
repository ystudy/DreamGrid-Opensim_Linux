using DreamGrid.Launcher.Core.Setup;

namespace DreamGrid.Launcher.Endpoints;

public static class SetupApi
{
    /// <summary>No authorization of its own: SetupGate admits /api/setup/* only under the setup page rules.</summary>
    public static void Map(WebApplication app) =>
        app.MapGet("/api/setup/progress", (SetupProgress progress, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(progress.Snapshot());
        });
}
