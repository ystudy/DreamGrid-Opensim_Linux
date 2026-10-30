using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// Makes the Staff and People pages not exist (404) on a Region Server and a Connected Server, which have no supervisors
/// (<see cref="AdminRule.HasStaffPages"/>). A resource filter, so it runs after the page's policy (an owner or staff member is
/// signed in) and before anything is bound or posted.
/// </summary>
public sealed class StaffPagesOnly : IResourceFilter
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
