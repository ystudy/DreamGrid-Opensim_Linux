using DreamGrid.Launcher.Dashboard;
using DreamGrid.Launcher.Security;

namespace DreamGrid.Launcher.Endpoints;

/// <summary><c>GET /api/dashboard</c>: the Home page's numbers, for its script to refresh. Admins only, like Home; counts only, no names.</summary>
public static class DashboardApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/dashboard", async (HttpContext ctx, DashboardSnapshot snapshot) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(await snapshot.CurrentAsync(ctx.RequestAborted));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
    }
}
