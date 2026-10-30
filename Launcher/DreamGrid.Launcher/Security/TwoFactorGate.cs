using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// Someone who must set up two-factor first may reach the two-factor page and sign out, nothing else: every other page
/// redirects to <see cref="EnrolPath"/>, and API calls get 403. Static files are served before this runs. See
/// <see cref="MustEnrol"/> for who that is.
/// </summary>
public sealed class TwoFactorGate(RequestDelegate next)
{
    public const string EnrolPath = "/profile/two-factor";

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path.Equals(EnrolPath, StringComparison.OrdinalIgnoreCase)
            || ctx.Request.Path.Equals("/logout", StringComparison.OrdinalIgnoreCase) || !MustEnrol(ctx))
        {
            await next(ctx);
            return;
        }

        if (ApiEndpoint.Is(ctx))
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        else
            ctx.Response.Redirect(EnrolPath);
    }

    private static readonly object ItemKey = new();

    /// <summary>
    /// Whether the signed-in staff member must set up two-factor before anything else (worked out once per request):
    /// <list type="bullet">
    /// <item>their cookie carries <see cref="StaffSignIn.EnrolClaim"/> (signed in with a password only while the owner required
    /// two-factor). The claim alone decides: such a cookie never opens anything else, even if two-factor was since turned on
    /// in another browser or the switch turned off (signing in again gives a cookie that matches);</item>
    /// <item>or the owner's switch is on, they are an admin or a supervisor (never the owner), and their two-factor isn't on
    /// (off, or can't be read): someone signed in before the switch was turned on, or whose two-factor was reset since.</item>
    /// </list>
    /// </summary>
    public static bool MustEnrol(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(ItemKey, out var known) && known is bool cached) return cached;
        var must = Decide(ctx);
        ctx.Items[ItemKey] = must;
        return must;
    }

    /// <summary>Forgets this request's answer, after the person's cookie or two-factor changed during it.</summary>
    public static void Forget(HttpContext ctx) => ctx.Items.Remove(ItemKey);

    private static bool Decide(HttpContext ctx)
    {
        var user = ctx.User;
        if (user.Identity is not { IsAuthenticated: true, AuthenticationType: AuthSchemes.Admin }) return false;
        if (user.HasClaim(c => c.Type == StaffSignIn.EnrolClaim)) return true;
        if (StaffSignIn.Read(user) is not { Role: StaffRole.Admin or StaffRole.Supervisor } claims) return false;
        var services = ctx.RequestServices;
        if (!services.GetRequiredService<Func<LauncherSettings>>()().Security.RequireTwoFactorForStaff) return false;
        return services.GetRequiredService<ITwoFactorStatus>().StateOf(claims.Principal) != TwoFactorState.On;
    }
}
