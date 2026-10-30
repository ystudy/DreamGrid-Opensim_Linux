using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// When the owner has set <see cref="SecuritySettings.AdminNetworks"/>, a caller outside those networks gets an empty 404 for
/// every request: the sign-in page, the pages, the APIs and the port-8001 command routes alike, so the site does not say it is
/// there or whether a cookie is valid. Static files are served before this runs, and the paths <see cref="Exempt"/> names are
/// never hidden. Loopback is always inside (<see cref="NetworkList.Allows"/>). Runs before authentication on purpose.
/// </summary>
public sealed class AdminNetworkFilter(RequestDelegate next, Func<LauncherSettings> settings)
{
    /// <summary>
    /// Paths open to every address whatever the list says: the setup pages (their own gate guards them; the list is empty during
    /// setup anyway). Plan 7b adds the pages guests and the public use.
    /// </summary>
    public static bool Exempt(PathString path) => path.StartsWithSegments("/setup");

    public Task InvokeAsync(HttpContext ctx)
    {
        var networks = settings().Security.AdminNetworks;
        if (networks is not { Count: > 0 } || Exempt(ctx.Request.Path)
            || (ctx.Connection.RemoteIpAddress is { } caller && NetworkList.Allows(networks, caller)))
            return next(ctx);

        // No address to judge counts as outside (Kestrel always knows it).
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
