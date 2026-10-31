using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Makes the guest pages not exist (404) on a Region Server and a Connected Server, which have no guest side
/// (<see cref="AdminRule.HasStaffPages"/>). A resource filter, so it runs after the page's policy and before anything is
/// bound or posted.
/// </summary>
public sealed class GuestSideOnly : IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (!AdminRule.HasStaffPages(context.HttpContext.RequestServices.GetRequiredService<Func<LauncherSettings>>()()))
            context.Result = new NotFoundResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
