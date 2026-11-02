using DreamGrid.Launcher.Splash;

namespace DreamGrid.Launcher.Endpoints;

/// <summary>
/// The viewer splash page's routes besides the page itself (spec 2026-11-01-viewer-splash-design §4.7, §6): the stylesheet
/// each design is drawn with, open to anyone like /welcome.
/// </summary>
public static class SplashApi
{
    public static void Map(WebApplication app)
    {
        // Only the current version is served: an old address after a publish is a 404, not an old layout.
        app.MapGet("/welcome/page-{version:int}.css", async (int version, HttpContext ctx, SplashSnapshot snapshot) =>
        {
            var view = await snapshot.CurrentAsync(ctx.RequestAborted);
            if (view.Version != version) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Text(view.Css, "text/css");
        }).AllowAnonymous();
    }
}
