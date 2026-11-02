using System.Text.Json;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Splash;
using DreamGrid.Launcher.Security;
using DreamGrid.Launcher.Splash;
using Microsoft.AspNetCore.Antiforgery;

namespace DreamGrid.Launcher.Endpoints;

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
        // Only the current version is served: an old address after a publish is a 404, not an old layout.
        app.MapGet("/welcome/page-{version:int}.css", async (int version, HttpContext ctx, SplashSnapshot snapshot) =>
        {
            var view = await snapshot.CurrentAsync(ctx.RequestAborted);
            if (view.Version != version) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Text(view.Css, "text/css");
        }).AllowAnonymous();

        var api = app.MapGroup("/api/splash").RequireAuthorization(AuthSchemes.AdminPolicy)
            .AddEndpointFilter(async (context, next) =>
                AdminRule.HasStaffPages(context.HttpContext.RequestServices.GetRequiredService<Func<LauncherSettings>>()())
                    ? await next(context) : Results.NotFound());

        api.MapGet("", (SplashStore store) => Results.Json(new
        {
            draft = store.Draft(),
            published = store.Published(),
            defaultDesign = SplashDefaults.Design(),
            limits = new
            {
                SplashRules.MinWidth, SplashRules.MaxWidth, SplashRules.MinHeight, SplashRules.MaxHeight,
                SplashRules.MaxBlocks, SplashRules.MaxLayer, SplashRules.MinBlockSize, SplashRules.MaxSeeThrough,
                SplashRules.MaxDarken, SplashRules.MaxPadding, SplashRules.MinFontSize, SplashRules.MaxFontSize,
                SplashRules.MaxText, SplashRules.MaxCaption, SplashRules.MaxHeading, SplashRules.MaxNewsText,
                SplashRules.MaxPlaceLine, SplashRules.MaxRegionName, SplashRules.MaxNews, SplashRules.MaxPlaces,
                SplashRules.SnapSizes, SplashRules.Languages,
            },
        }, Json));

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
                return Results.Json(new { problem = "There is no draft to publish." }, Json, statusCode: StatusCodes.Status409Conflict);
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
