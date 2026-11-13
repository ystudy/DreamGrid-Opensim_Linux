using System.Security.Claims;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GridGate.Launcher.Iar;

/// <summary>
/// Makes the resident's inventory backup page not exist (404) unless this install has a guest side and the signed-in resident may
/// download or upload (the grid's switch on, and theirs not off). Runs before the antiforgery check, so a post from anyone else
/// answers 404 too and nothing is bound or saved.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class IarPagesOnlyAttribute : Attribute, IAuthorizationFilter, IOrderedFilter
{
    public int Order => int.MinValue + 1;

    public static bool MayDownload(LauncherSettings s, IarPersonRights r) => s.Iar.DownloadsOn && !r.DownloadOff;
    public static bool MayUpload(LauncherSettings s, IarPersonRights r) => s.Iar.UploadsOn && !r.UploadOff;

    /// <summary>The same rule for the link on the resident's home page.</summary>
    public static bool Shown(LauncherSettings s, IarRights rights, ClaimsPrincipal user) =>
        AdminRule.HasStaffPages(s) && GuestSignIn.Read(user) is { } guest
        && rights.For(guest.Principal) is var r && (MayDownload(s, r) || MayUpload(s, r));

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        if (!Shown(services.GetRequiredService<Func<LauncherSettings>>()(), services.GetRequiredService<IarRights>(), context.HttpContext.User))
            context.Result = new NotFoundResult();
    }
}
