using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Security;

namespace DreamGrid.Launcher.Endpoints;

/// <param name="State">The <see cref="RegionState"/> name in lower case, or "reserved" for a deleted region.</param>
/// <param name="StateText">The dashboard's words for the state.</param>
/// <param name="RunsWith">The other live regions in its program.</param>
/// <param name="Reserved">True for a deleted region, which keeps its map spot until erased.</param>
public sealed record MapRegion(Guid Id, string Name, int X, int Y, int Size, string State, string StateText,
    IReadOnlyList<string> RunsWith, bool Reserved);

/// <summary>The Regions page's data: map tiles through the launcher (never Robust's port in the browser), the regions and the placement advice. Admin only.</summary>
public static class RegionsApi
{
    /// <summary>The named HTTP client for Robust's map tiles.</summary>
    public const string RobustMapClient = "robust-map";

    private const int MaxCoordinate = 65535;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/map/tile/{z:int}/{x:int}/{y:int}", async (int z, int x, int y, IGridController grid, IHttpClientFactory http, HttpContext ctx) =>
        {
            // A zoom z tile spans 2^(z-1) squares, so its corner is a multiple of that. Only validated integers reach the URL.
            if (z is < 1 or > 8 || x is < 0 or > MaxCoordinate || y is < 0 or > MaxCoordinate)
                return Results.BadRequest();
            var span = 1 << (z - 1);
            if (x % span != 0 || y % span != 0)
                return Results.BadRequest();

            // Read per request, so a changed port applies at once.
            var port = grid.CurrentSettings().RobustPublicPort;
            try
            {
                using var client = http.CreateClient(RobustMapClient);
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/map-{z}-{x}-{y}-objects.jpg", ctx.RequestAborted);
                // A missing tile can come back as a 200 with text/plain and an empty or short body.
                var type = response.Content.Headers.ContentType?.MediaType;
                if (!response.IsSuccessStatusCode || type is not ("image/jpeg" or "image/png"))
                    return Results.NotFound();
                var bytes = await response.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                if (bytes.Length == 0)
                    return Results.NotFound();
                ctx.Response.Headers.CacheControl = "private, max-age=60";
                return Results.File(bytes, type);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ctx.RequestAborted.IsCancellationRequested)
            {
                return Results.NotFound();
            }
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // A region type's picture; the page shows a coloured square when there is none. Only built-in ids resolve.
        app.MapGet("/regions/types/{id}.png", (string id, LauncherPaths paths) =>
            RegionTypes.Picture(paths, id) is { } file ? Results.File(file, "image/png") : Results.NotFound())
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        app.MapGet("/api/regions", (HttpContext ctx, IGridController grid) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var live = grid.GetStatus().Regions.ToDictionary(r => r.Id);
            var regions = grid.CurrentSettings().Regions.Select(r =>
            {
                if (r.DeletedAt is not null)
                    return new MapRegion(r.Uuid, r.Name, r.X, r.Y, r.Size, "reserved", "Reserved", [], true);
                // A live region the status does not know yet counts as stopped.
                live.TryGetValue(r.Uuid, out var status);
                var state = status?.State ?? RegionState.Stopped;
                return new MapRegion(r.Uuid, r.Name, r.X, r.Y, r.Size, state.ToString().ToLowerInvariant(),
                    StatusApi.RegionStateText(state), status?.RunsWith ?? [], false);
            }).ToList();
            return Results.Json(new { regions });
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        app.MapGet("/api/regions/placement", (int x, int y, int size, Guid? ignore, IGridController grid) =>
        {
            if (size is < 1 or > RegionPlacement.MaxSize || x < 0 || y < 0 || x > MaxCoordinate - size || y > MaxCoordinate - size)
                return Results.Json(new { ok = false, text = "That spot is off the map." }, statusCode: StatusCodes.Status400BadRequest);
            var check = RegionPlacement.Check(grid.CurrentSettings(), x, y, size, ignore);
            return Results.Json(new { ok = check.Ok, text = RegionAdvice.Placement(check) });
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
    }
}
