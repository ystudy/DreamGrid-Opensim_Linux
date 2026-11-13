using GridGate.Launcher.Core.ModelImport;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GridGate.Launcher.ModelImport;

/// <summary>
/// Makes the upload pages not exist (404) unless this install can take uploads (<see cref="ImportLimitsRules.UploadAvailable"/>)
/// and the signed-in guest may upload (<see cref="UploadRights"/>). Runs right after <see cref="GuestSideOnly"/> and before the
/// antiforgery check, so a post from anyone else answers 404 too and nothing is bound or saved.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class UploadPagesOnlyAttribute : Attribute, IAuthorizationFilter, IOrderedFilter
{
    public int Order => int.MinValue + 1;

    /// <summary>The same rule for the link on the guest's home page.</summary>
    public static bool Shown(LauncherSettings settings, UploadRights rights, System.Security.Claims.ClaimsPrincipal user) =>
        ImportLimitsRules.UploadAvailable(settings) && GuestSignIn.Read(user) is { } guest && rights.Has(guest.Principal);

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        if (!Shown(services.GetRequiredService<Func<LauncherSettings>>()(), services.GetRequiredService<UploadRights>(), context.HttpContext.User))
            context.Result = new NotFoundResult();
    }
}
