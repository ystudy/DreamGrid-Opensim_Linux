using Microsoft.AspNetCore.Authentication;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// Access rules around setup. Before the owner exists only the setup link (and its cookie) opens /setup*.
/// Afterwards, until setup is finished, /setup* is for the signed-in admin: anyone else is sent to sign in first (the
/// sign-in cookie lasts only for the browser session, so this is how an interrupted setup is resumed), and the admin's
/// home "/" leads back to the checklist. Once setup is finished /setup* shows "Setup is already finished". Anyone not signed in
/// as staff who opens "/" gets the public front page (on a plan with a guest side, once the owner exists).
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
        // are sent to their own start page rather than to the sign-in page. Anyone else gets the public front page, when the
        // plan has a guest side (the address stays "/"; setup being unfinished makes no difference to a visitor).
        if (path == "/")
        {
            if (!await IsAdminAsync(ctx))
            {
                var user = (await ctx.AuthenticateAsync(AuthSchemes.Admin)).Principal;
                var role = user is null ? StaffRole.None : StaffSignIn.RoleOf(user);
                if (role != StaffRole.None)
                    ctx.Response.Redirect(StaffSignIn.Home(role));
                else if (AdminRule.HasStaffPages(s))
                    await ShowFrontPageAsync(ctx);
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

    /// <summary>The path the bare root shows to someone who isn't signed in as staff.</summary>
    public const string FrontPage = "/home";

    /// <summary>
    /// Answers the request as the front page without a redirect: the path changes and the endpoint chosen for "/" is dropped, so
    /// the routing that runs next (<c>UseRouting</c> after this gate) picks the front page.
    /// </summary>
    private Task ShowFrontPageAsync(HttpContext ctx)
    {
        ctx.SetEndpoint(null);
        ctx.Request.RouteValues.Clear();
        ctx.Request.Path = FrontPage;
        return next(ctx);
    }

    /// <summary>
    /// Whether this is a GET of the bare "/" with none of the port-8001 keys: the browser's home, as opposed to a command of
    /// the legacy contract (which always passes this gate untouched).
    /// </summary>
    public static bool IsFrontPageRequest(HttpRequest r) =>
        HttpMethods.IsGet(r.Method) && r.Path == "/" && !LegacyKeys.Any(r.Query.ContainsKey);

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
        return path == "/" && !IsFrontPageRequest(r);
    }
}
