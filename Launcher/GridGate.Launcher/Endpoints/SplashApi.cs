using System.Text.Json;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Splash;
using GridGate.Launcher.Core.Theme;
using GridGate.Launcher.Security;
using GridGate.Launcher.Splash;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

namespace GridGate.Launcher.Endpoints;

/// <summary>
/// The viewer splash page's routes besides the page itself (spec 2026-11-01-viewer-splash-design §4.1, §4.7, §4.8):
/// the stylesheet each design is drawn with, open to anyone like /welcome; and the editor's API under /api/splash, for the
/// owner and admins only (supervisors can't), with the antiforgery header on every change. The API is 404 where there is no
/// guest side, since the built-in page is all such a machine shows. Saving a draft isn't audited here (AuditMiddleware logs
/// every staff change request); publishing and discarding are.
/// </summary>
public static class SplashApi
{
    /// <summary>A design body's largest size: 100 blocks of the longest texts fit well inside.</summary>
    public const int MaxBodyBytes = 512 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        // The current version, and the one before it: a viewer may keep the page for a minute after a publish, and the
        // stylesheet that page names must still be there. Anything else is a 404, not an old layout.
        app.MapGet("/welcome/page-{version:int}.css", async (int version, HttpContext ctx, SplashSnapshot snapshot) =>
        {
            if (await snapshot.CssAsync(version, ctx.RequestAborted) is not { } css) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Text(css, "text/css");
        }).AllowAnonymous();

        var api = app.MapGroup("/api/splash").RequireAuthorization(AuthSchemes.AdminPolicy)
            .AddEndpointFilter(async (context, next) =>
                AdminRule.HasStaffPages(context.HttpContext.RequestServices.GetRequiredService<Func<LauncherSettings>>()())
                    ? await next(context) : Results.NotFound());
        MapPictures(app, api);
        MapTheme(app);

        api.MapGet("", async (SplashStore store, IGridRegions regions, HttpContext ctx) => Results.Json(new
        {
            // The whole grid's regions, for the Places to go picker; empty when the list can't be read.
            regions = (await regions.ListAsync(ctx.RequestAborted)).Regions.Select(r => new { r.Id, r.Name }),
            draft = store.Draft(),
            // What the next save is based on: the draft's version, or the last one when the draft was discarded.
            draftVersion = store.DraftVersion(),
            published = store.Published(),
            defaultDesign = SplashDefaults.Design(),
            limits = new
            {
                SplashRules.MinWidth, SplashRules.MaxWidth, SplashRules.MinHeight, SplashRules.MaxHeight,
                SplashRules.MaxBlocks, SplashRules.MaxLayer, SplashRules.MinBlockSize, SplashRules.MaxSeeThrough,
                SplashRules.MaxDarken, SplashRules.MaxPadding, SplashRules.MinFontSize, SplashRules.MaxFontSize, SplashRules.MaxRotate,
                SplashRules.MaxText, SplashRules.MaxCaption, SplashRules.MaxHeading, SplashRules.MaxNewsText,
                SplashRules.MaxPlaceLine, SplashRules.MaxRegionName, SplashRules.MaxNews, SplashRules.MaxPlaces,
                SplashRules.SnapSizes, SplashRules.Languages,
            },
        }, Json));

        // The draft drawn by the public page's own code, for the editor's canvas and the preview. The address carries the
        // draft's version only so the browser fetches it again after a save.
        api.MapGet("/draft.css", async (HttpContext ctx, SplashSnapshot snapshot) =>
        {
            var view = await snapshot.WorkingAsync(ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Text(view.Css, "text/css");
        });

        api.MapPut("/draft", async (int basedOn, HttpContext ctx, IAntiforgery antiforgery, SplashStore store) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx)) return TokenProblem();
            var (design, problem) = await ReadDesignAsync(ctx);
            if (design is null) return problem!;
            return store.SaveDraft(design, basedOn, AuditActor.Of(ctx.User).Name) switch
            {
                SplashSaveResult.Saved s => Results.Json(s.Draft, Json),
                SplashSaveResult.Conflict c => Results.Json(new { currentVersion = c.CurrentVersion }, Json, statusCode: StatusCodes.Status409Conflict),
                SplashSaveResult.Invalid i => Results.Json(new { problems = i.Problems }, Json, statusCode: StatusCodes.Status400BadRequest),
                _ => throw new InvalidOperationException("Unknown save result."),
            };
        });

        api.MapPost("/publish", async (HttpContext ctx, IAntiforgery antiforgery, SplashStore store, SplashSnapshot snapshot, AuditLog audit) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx)) return TokenProblem();
            var (name, id, role) = AuditActor.Of(ctx.User);
            if (store.Publish(name) is not { } doc)
                return Results.Json(new
                {
                    problem = store.Draft() is null ? "There is no draft to publish." : "The draft uses a picture that is no longer there. Choose another picture and save again.",
                }, Json, statusCode: StatusCodes.Status409Conflict);
            snapshot.Invalidate();
            audit.Write(new AuditEntry(name, id, role, ctx.Connection.RemoteIpAddress?.ToString(), "splash-published", $"version {doc.Version}", "ok"));
            return Results.Json(doc, Json);
        });

        api.MapPost("/discard", async (HttpContext ctx, IAntiforgery antiforgery, SplashStore store, AuditLog audit) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx)) return TokenProblem();
            store.Discard();
            var (name, id, role) = AuditActor.Of(ctx.User);
            audit.Write(new AuditEntry(name, id, role, ctx.Connection.RemoteIpAddress?.ToString(), "splash-draft-discarded", null, "ok"));
            return Results.NoContent();
        });
    }

    /// <summary>A picture's address never changes what it holds (a new picture gets a new name), so it may be kept for a year.</summary>
    private const string Immutable = "public, max-age=31536000, immutable";

    /// <summary>
    /// The splash pictures (spec §4.4): staff upload (the raw file as the body), list, fetch and delete them; anyone may fetch
    /// a picture the published page uses, and nothing else.
    /// </summary>
    private static void MapPictures(WebApplication app, RouteGroupBuilder api)
    {
        app.MapGet("/welcome/images/{name}", async (string name, HttpContext ctx, SplashSnapshot snapshot,
            [FromKeyedServices("splash")] PictureStore pictures) =>
        {
            var view = await snapshot.CurrentAsync(ctx.RequestAborted);
            if (view.IsDefault || !SplashPictures.UsedBy(view.Design).Contains(name) || pictures.Open(name) is not { } file)
                return Results.NotFound();
            ctx.Response.Headers.CacheControl = Immutable;
            return Results.File(file.Path, file.ContentType);
        }).AllowAnonymous();

        api.MapGet("/pictures", (SplashStore store, [FromKeyedServices("splash")] PictureStore pictures) =>
        {
            var inUse = SplashPictures.InUse(store);
            return Results.Json(new
            {
                pictures = pictures.List().Select(p => new { p.Name, p.Label, p.Width, p.Height, p.Bytes, p.Added, p.AddedBy, inUse = inUse.Contains(p.Name) }),
            }, Json);
        });

        api.MapGet("/pictures/{name}", (string name, [FromKeyedServices("splash")] PictureStore pictures) =>
            pictures.Open(name) is { } file ? Results.File(file.Path, file.ContentType) : Results.NotFound());

        api.MapPost("/pictures", async (string? label, HttpContext ctx, IAntiforgery antiforgery,
            [FromKeyedServices("splash")] PictureStore pictures, AuditLog audit) =>
        {
            var (result, problem) = await UploadAsync(ctx, antiforgery, pictures, label);
            if (result is null) return problem!;
            var (name, id, role) = AuditActor.Of(ctx.User);
            audit.Write(new AuditEntry(name, id, role, ctx.Connection.RemoteIpAddress?.ToString(), "splash-image-uploaded",
                $"{result.Name} ({result.Label})", "ok"));
            return Results.Json(result, Json);
        });

        // A region's map tile, fetched from Robust as the map page's tile proxy does, then kept like any upload (redrawn, new name).
        api.MapPost("/pictures/from-region/{id:guid}", async (Guid id, HttpContext ctx, IAntiforgery antiforgery, IGridRegions regions,
            IGridController grid, IHttpClientFactory http, [FromKeyedServices("splash")] PictureStore pictures, AuditLog audit) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx)) return TokenProblem();
            var list = await regions.ListAsync(ctx.RequestAborted);
            if (!list.Available)
                return Results.Json(new { problem = "The grid's region list can't be read right now. Try again in a minute." }, Json,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            if (list.Regions.FirstOrDefault(r => r.Id == id) is not { } region)
                return Results.Json(new { problem = "That region isn't registered with the grid." }, Json, statusCode: StatusCodes.Status404NotFound);

            byte[] tile;
            try
            {
                using var client = http.CreateClient(RegionsApi.RobustMapClient);
                var port = grid.CurrentSettings().RobustPublicPort;
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/map-1-{region.X}-{region.Y}-objects.jpg", ctx.RequestAborted);
                // A missing tile can come back as a 200 with text/plain and an empty body.
                tile = response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is "image/jpeg" or "image/png"
                    ? await response.Content.ReadAsByteArrayAsync(ctx.RequestAborted) : [];
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ctx.RequestAborted.IsCancellationRequested)
            {
                tile = [];
            }
            if (tile.Length == 0)
                return Results.Json(new { problem = "The grid has no map picture for that region yet." }, Json, statusCode: StatusCodes.Status404NotFound);

            var (result, problem) = await UploadAsync(ctx, antiforgery, pictures, $"Map of {region.Name}", new MemoryStream(tile));
            if (result is null) return problem!;
            var (name, actorId, role) = AuditActor.Of(ctx.User);
            audit.Write(new AuditEntry(name, actorId, role, ctx.Connection.RemoteIpAddress?.ToString(), "splash-image-uploaded",
                $"{result.Name} ({result.Label})", "ok"));
            return Results.Json(result, Json);
        });

        api.MapDelete("/pictures/{name}", async (string name, HttpContext ctx, IAntiforgery antiforgery, SplashStore store,
            [FromKeyedServices("splash")] PictureStore pictures, AuditLog audit) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx)) return TokenProblem();
            if (pictures.Get(name) is not { } picture) return Results.NotFound();
            if (!store.RemovePictureIfUnused(name, pictures.Delete))
                return pictures.Get(name) is null
                    ? Results.NotFound()
                    : Results.Json(new { problem = "The page still uses that picture." }, Json, statusCode: StatusCodes.Status409Conflict);
            var (actor, id, role) = AuditActor.Of(ctx.User);
            audit.Write(new AuditEntry(actor, id, role, ctx.Connection.RemoteIpAddress?.ToString(), "splash-image-deleted",
                $"{picture.Name} ({picture.Label})", "ok"));
            return Results.NoContent();
        });
    }

    /// <summary>
    /// The grid's logo (spec §4.2): the owner and admins set or remove it; anyone may fetch the current one, and nothing else.
    /// Stored in the theme's picture store, checked and redrawn like any upload. Not there where there is no guest side.
    /// </summary>
    private static void MapTheme(WebApplication app)
    {
        app.MapGet("/theme/images/{name}", (string name, HttpContext ctx, ThemeStore theme,
            [FromKeyedServices("theme")] PictureStore pictures) =>
        {
            if (theme.Logo() != name || pictures.Open(name) is not { } file) return Results.NotFound();
            ctx.Response.Headers.CacheControl = Immutable;
            return Results.File(file.Path, file.ContentType);
        }).AllowAnonymous();

        var api = app.MapGroup("/api/theme").RequireAuthorization(AuthSchemes.AdminPolicy)
            .AddEndpointFilter(async (context, next) =>
                AdminRule.HasStaffPages(context.HttpContext.RequestServices.GetRequiredService<Func<LauncherSettings>>()())
                    ? await next(context) : Results.NotFound());

        api.MapPost("/logo", async (string? label, HttpContext ctx, IAntiforgery antiforgery, ThemeStore theme,
            [FromKeyedServices("theme")] PictureStore pictures, SplashSnapshot snapshot, AuditLog audit) =>
        {
            var (result, problem) = await UploadAsync(ctx, antiforgery, pictures, label);
            if (result is null) return problem!;
            var old = theme.Logo();
            theme.SetLogo(result.Name);
            if (old is not null) pictures.Delete(old);
            snapshot.Invalidate();
            var (name, id, role) = AuditActor.Of(ctx.User);
            audit.Write(new AuditEntry(name, id, role, ctx.Connection.RemoteIpAddress?.ToString(), "theme-logo-set", $"{result.Name} ({result.Label})", "ok"));
            return Results.Json(result, Json);
        });

        api.MapDelete("/logo", async (HttpContext ctx, IAntiforgery antiforgery, ThemeStore theme,
            [FromKeyedServices("theme")] PictureStore pictures, SplashSnapshot snapshot, AuditLog audit) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx)) return TokenProblem();
            var old = theme.Logo();
            theme.SetLogo(null);
            if (old is not null) pictures.Delete(old);
            snapshot.Invalidate();
            var (name, id, role) = AuditActor.Of(ctx.User);
            audit.Write(new AuditEntry(name, id, role, ctx.Connection.RemoteIpAddress?.ToString(), "theme-logo-removed", old, "ok"));
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Checks the antiforgery header before reading any of the body, then hands the body to <paramref name="pictures"/>. The
    /// request size limit is set just above <see cref="PictureStore.MaxUploadBytes"/> for this request only, so a bigger body
    /// is cut off by the server as well as by the store.
    /// </summary>
    internal static async Task<(StoredPicture?, IResult?)> UploadAsync(HttpContext ctx, IAntiforgery antiforgery, PictureStore pictures,
        string? label, Stream? source = null)
    {
        if (!await antiforgery.IsRequestValidAsync(ctx)) return (null, TokenProblem());
        if (source is null && ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = PictureStore.MaxUploadBytes + 1;
        PictureResult result;
        try
        {
            result = await pictures.AddAsync(source ?? ctx.Request.Body, label, AuditActor.Of(ctx.User).Name, ctx.RequestAborted);
        }
        catch (BadHttpRequestException e)
        {
            return (null, Results.StatusCode(e.StatusCode));
        }
        return result switch
        {
            PictureResult.Added a => (a.Picture, null),
            PictureResult.Refused r => (null, Results.Json(new { problem = r.Message }, Json, statusCode: StatusCodes.Status400BadRequest)),
            _ => (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge)),
        };
    }

    private static IResult TokenProblem() =>
        Results.BadRequest("The request verification token is missing or wrong. Reload the page and try again.");

    /// <summary>The body as a design, read up to <see cref="MaxBodyBytes"/>; otherwise the answer to send instead.</summary>
    private static async Task<(SplashDesign? Design, IResult? Problem)> ReadDesignAsync(HttpContext ctx)
    {
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            if (body.Length + read > MaxBodyBytes)
                return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
            body.Write(buffer, 0, read);
        }
        body.Position = 0;
        try
        {
            if (await JsonSerializer.DeserializeAsync<SplashDesign>(body, Json, ctx.RequestAborted) is { } design)
                return (design, null);
        }
        catch (JsonException)
        {
        }
        return (null, Results.Json(new { problems = new[] { "The page could not be read." } }, Json, statusCode: StatusCodes.Status400BadRequest));
    }
}
