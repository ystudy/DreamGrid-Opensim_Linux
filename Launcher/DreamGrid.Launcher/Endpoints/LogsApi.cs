using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Security;

namespace DreamGrid.Launcher.Endpoints;

public static class LogsApi
{
    private const int MaxLines = 2000;

    public static void Map(WebApplication app) =>
        app.MapGet("/api/logs", (LogReader logs, string? source, int? lines, string? level) =>
        {
            // The source is matched against the known names, never used as a path.
            if (source is null || !logs.Sources().Contains(source, StringComparer.Ordinal))
                return Results.NotFound();
            try
            {
                return Results.Ok(logs.Tail(source, Math.Clamp(lines ?? 200, 1, MaxLines), string.IsNullOrEmpty(level) ? null : level));
            }
            catch (ArgumentException)
            {
                return Results.BadRequest("The level must be DEBUG, INFO, WARN or ERROR.");
            }
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
}
