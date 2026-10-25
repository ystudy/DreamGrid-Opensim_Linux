using Microsoft.AspNetCore.Authentication;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// Access rules around setup. Before the owner exists only the setup link (and its cookie) opens /setup*;
/// afterwards /setup* is closed except for the signed-in admin until setup is finished.
/// Static files (served before this) and the port-8001 legacy routes always pass.
/// </summary>
public sealed class SetupGate(RequestDelegate next, Func<LauncherSettings> settings)
{
    private static readonly string[] LegacyKeys = ["alt", "type", "command"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (AlwaysOpen(ctx.Request))
        {
            await next(ctx);
            return;
        }

        var s = settings();
        var path = ctx.Request.Path;
        var isSetup = path.StartsWithSegments("/setup") || path.StartsWithSegments("/api/setup");

        if (!s.Setup.OwnerCreated)
        {
            if (HttpMethods.IsGet(ctx.Request.Method) && path.StartsWithSegments("/setup") &&
                ctx.Request.Query.TryGetValue("token", out var given) &&
                SetupTokenCookie.Matches(s.SetupToken, given.ToString()))
            {
                // The token leaves the URL here; afterwards only the cookie carries it.
                SetupTokenCookie.Issue(ctx.Response, s.SetupToken!);
                ctx.Response.Redirect("/setup");
                return;
            }

            if (isSetup && SetupTokenCookie.Matches(s.SetupToken, ctx.Request.Cookies[SetupTokenCookie.Name]))
            {
                await next(ctx);
                return;
            }

            ctx.Response.Redirect("/waiting");
            return;
        }

        if (isSetup)
        {
            if (!s.Setup.Finished && await IsAdminAsync(ctx))
                await next(ctx);
            else
                ctx.Response.Redirect("/setup-done");
            return;
        }

        // The legacy root handler has no authorization of its own; the bare "/" is the admin home.
        if (path == "/" && !await IsAdminAsync(ctx))
        {
            await ctx.ChallengeAsync(AuthSchemes.Admin);
            return;
        }

        await next(ctx);
    }

    private static async Task<bool> IsAdminAsync(HttpContext ctx)
    {
        var result = await ctx.AuthenticateAsync(AuthSchemes.Admin);
        return result.Succeeded && result.Principal!.IsInRole(AuthSchemes.AdminRole);
    }

    private static bool AlwaysOpen(HttpRequest r)
    {
        var path = r.Path;
        if (path.StartsWithSegments("/welcome") || path.StartsWithSegments("/waiting") ||
            path.StartsWithSegments("/setup-done") || path.StartsWithSegments("/api/status") ||
            path.StartsWithSegments("/dreamgrid"))
            return true;
        if (path != "/") return false;
        return !HttpMethods.IsGet(r.Method) || LegacyKeys.Any(r.Query.ContainsKey);
    }
}
