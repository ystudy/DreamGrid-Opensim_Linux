namespace DreamGrid.Launcher.Security;

/// <summary>
/// Someone signed in only to set up two-factor (their cookie carries <see cref="StaffSignIn.EnrolClaim"/>, given while the owner
/// requires two-factor for staff) may reach the two-factor page and sign out, nothing else: every other page redirects to
/// <see cref="EnrolPath"/>, and API calls get 403. Static files are served before this runs. The claim alone decides: a
/// cookie issued to enrol never opens anything else, even if two-factor was since turned on in another browser or the switch
/// was turned off (signing in again gives a cookie that matches).
/// </summary>
public sealed class TwoFactorGate(RequestDelegate next)
{
    public const string EnrolPath = "/profile/two-factor";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var user = ctx.User;
        if (user.Identity is not { IsAuthenticated: true, AuthenticationType: AuthSchemes.Admin } || !user.HasClaim(c => c.Type == StaffSignIn.EnrolClaim)
            || ctx.Request.Path.Equals(EnrolPath, StringComparison.OrdinalIgnoreCase) || ctx.Request.Path.Equals("/logout", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        if (ApiEndpoint.Is(ctx))
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        else
            ctx.Response.Redirect(EnrolPath);
    }
}
