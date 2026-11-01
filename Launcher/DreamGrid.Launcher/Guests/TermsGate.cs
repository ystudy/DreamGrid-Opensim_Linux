using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// On the guest pages: a signed-in guest who has not accepted the current version of the operator's Terms is sent to
/// <c>/guest/terms</c> first. Nothing is asked while there is no Terms text. The Terms page itself and sign-out are left
/// alone, so a guest can always read the Terms or leave.
/// </summary>
public sealed class TermsGate : IAsyncPageFilter
{
    private static readonly string[] Open = ["/Guest/Terms", "/Guest/SignOut", "/Guest/SignIn", "/Guest/SignInCode", "/Guest/ForgotPassword", "/Guest/ResetPassword", "/Guest/Confirm", "/Guest/ConfirmEmail", "/Guest/Connect"];

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        var page = context.ActionDescriptor.ViewEnginePath;
        if (!Open.Contains(page, StringComparer.OrdinalIgnoreCase)
            && GuestSignIn.Read(http.User) is { } guest
            && LegalPages.Get(http.RequestServices.GetRequiredService<Func<LauncherSettings>>()(), LegalPages.Terms) is { } terms
            && !http.RequestServices.GetRequiredService<TermsAcceptances>().HasAccepted(guest.Principal, terms.Version))
        {
            // Back to the same page afterwards, but only for a GET: a POST can't be repeated by a redirect.
            var back = HttpMethods.IsGet(http.Request.Method) ? http.Request.PathBase + http.Request.Path + http.Request.QueryString : "/guest";
            context.Result = new RedirectResult("/guest/terms?ReturnUrl=" + Uri.EscapeDataString(back));
            return;
        }
        await next();
    }
}
