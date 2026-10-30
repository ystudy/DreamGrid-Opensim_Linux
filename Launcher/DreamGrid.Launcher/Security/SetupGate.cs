using Microsoft.AspNetCore.Authentication;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// Access rules around setup. Before the owner exists only the setup link (and its cookie) opens /setup*.
/// Afterwards, until setup is finished, /setup* is for the signed-in admin: anyone else is sent to sign in first (the
/// sign-in cookie lasts only for the browser session, so this is how an interrupted setup is resumed), and the admin's
/// home "/" leads back to the checklist. Once setup is finished /setup* shows "Setup is already finished".
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
            if (s.Setup.Finished)
                ctx.Response.Redirect("/setup-done");
            else if (await IsAdminAsync(ctx))
                await next(ctx);
            else if (path.StartsWithSegments("/api"))
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; // API callers never get a redirect
            else
                ctx.Response.Redirect("/login?ReturnUrl=%2Fsetup"); // no dg-setup cookie: the token is spent
            return;
        }

        // The legacy root handler has no authorization of its own; the bare "/" is the admin home. Other staff (a supervisor)
        // are sent to their own start page rather than to the sign-in page.
        if (path == "/")
        {
            if (!await IsAdminAsync(ctx))
            {
                var user = (await ctx.AuthenticateAsync(AuthSchemes.Admin)).Principal;
                var role = user is null ? StaffRole.None : StaffSignIn.RoleOf(user);
                if (role != StaffRole.None)
                    ctx.Response.Redirect(StaffSignIn.Home(role));
                else
                    await ctx.ChallengeAsync(AuthSchemes.Admin);
                return;
            }
            if (!s.Setup.Finished)
            {
                ctx.Response.Redirect("/setup");
                return;
            }
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
