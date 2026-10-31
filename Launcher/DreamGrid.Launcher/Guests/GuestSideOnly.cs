using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Makes the guest pages not exist (404) on a Region Server and a Connected Server, which have no guest side
/// (<see cref="AdminRule.HasStaffPages"/>). The page's policy (the authorization middleware) runs first. An authorization
/// filter ordered first, so it also runs before the antiforgery check: a post without a token answers 404 too, not 400,
/// and nothing is bound or posted.
/// </summary>
public sealed class GuestSideOnly : IAuthorizationFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!AdminRule.HasStaffPages(context.HttpContext.RequestServices.GetRequiredService<Func<LauncherSettings>>()()))
            context.Result = new NotFoundResult();
    }
}
