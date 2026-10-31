using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// When the owner has set <see cref="SecuritySettings.AdminNetworks"/>, a caller outside those networks gets an empty 404 for
/// every request to the staff side: the sign-in page, the pages, the APIs and the port-8001 command routes alike, so the site
/// does not say it is there or whether a cookie is valid. Static files are served before this runs, and the paths
/// <see cref="Exempt"/> names are never hidden: a caller outside the list who asks for one passes with
/// <see cref="OutsideKey"/> set, which makes <see cref="StaffPrincipalValidator"/> ignore the staff cookie. Loopback is always
/// inside (<see cref="NetworkList.Allows"/>). Runs before authentication on purpose.
/// </summary>
public sealed class AdminNetworkFilter(RequestDelegate next, Func<LauncherSettings> settings)
{
    /// <summary>The <c>HttpContext.Items</c> key set (to true) for a caller outside the list on an exempt path.</summary>
    public const string OutsideKey = "DreamGrid.OutsideAdminNetworks";

    /// <summary>
    /// Whether the request is open to every address whatever the list says: the setup pages (their own gate guards them; the
    /// list is empty during setup anyway) and, only on a plan with a guest side (<see cref="AdminRule.HasStaffPages"/>), the
    /// pages guests and the public use: the front page ("/" itself, a GET without the port-8001 keys, and "/home"), the guest
    /// pages, the legal and contact pages, sign-up and the bot-protection endpoints.
    /// </summary>
    public static bool Exempt(HttpRequest r)
    {
        var path = r.Path;
        if (path.StartsWithSegments("/setup")) return true;
        if (!AdminRule.HasStaffPages(r.HttpContext.RequestServices.GetRequiredService<Func<LauncherSettings>>()())) return false;
        return SetupGate.IsFrontPageRequest(r) || PublicPaths.Any(p => path.StartsWithSegments(p));
    }

    private static readonly string[] PublicPaths =
        ["/home", "/guest", "/terms", "/privacy", "/cookies", "/contact", "/signup", "/api/bots"];

    public Task InvokeAsync(HttpContext ctx)
    {
        var networks = settings().Security.AdminNetworks;
        if (networks is not { Count: > 0 }
            || (ctx.Connection.RemoteIpAddress is { } caller && NetworkList.Allows(networks, caller)))
            return next(ctx);

        // No address to judge counts as outside (Kestrel always knows it).
        if (!Exempt(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        ctx.Items[OutsideKey] = true;
        return next(ctx);
    }
}
