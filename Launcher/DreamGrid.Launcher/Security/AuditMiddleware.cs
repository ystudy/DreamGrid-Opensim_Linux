using System.Security.Claims;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// Who a signed-in staff cookie belongs to, as the audit log names them. The one place that turns the cookie into a role
/// name, so a change to the cookie's claims changes only this.
/// </summary>
public static class AuditActor
{
    /// <summary>The name, principal id and role of the person behind <paramref name="user"/>.</summary>
    public static (string Name, Guid? Id, string Role) Of(ClaimsPrincipal user, LauncherSettings settings)
    {
        Guid? id = Guid.TryParse(user.FindFirstValue(AdminSignIn.PrincipalClaim), out var parsed) ? parsed : null;
        var owner = id is not null && settings.Setup.Owner?.PrincipalId == id;
        return (LogText.Clean(user.Identity?.Name, 64), id, owner ? StaffRole.Owner.AuditName() : StaffRole.Admin.AuditName());
    }
}

/// <summary>
/// Writes an audit entry for every change request (anything but GET, HEAD and OPTIONS) from a signed-in staff member, after the
/// response so the status is known. The target is the method and path only: the query string can carry a password.
/// Runs after authentication, so the cookie has been read; an anonymous request is not staff and is not recorded here.
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next, AuditLog audit, Func<LauncherSettings> settings)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var status = StatusCodes.Status500InternalServerError;
        try
        {
            await next(ctx);
            status = ctx.Response.StatusCode;
        }
        finally
        {
            Record(ctx, status);
        }
    }

    private void Record(HttpContext ctx, int status)
    {
        var request = ctx.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)) return;
        var user = ctx.User;
        if (user.Identity is not { IsAuthenticated: true, AuthenticationType: AuthSchemes.Admin } || !user.IsInRole(AuthSchemes.AdminRole)) return;

        var (name, id, role) = AuditActor.Of(user, settings());
        audit.Write(new AuditEntry(name, id, role, ctx.Connection.RemoteIpAddress?.ToString(), "request",
            LogText.Clean(request.Method + " " + request.PathBase + request.Path, 200), status.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
