using System.Globalization;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Resident;

/// <summary>
/// On a resident page, picks the language (<see cref="LanguagePick"/>), sets the culture for dates and numbers, and leaves the code
/// in <c>HttpContext.Items</c> for <see cref="Current"/>. Staff pages never get this filter and stay English.
/// </summary>
/// <param name="welcome">Whether this is the welcome page, which takes only the <c>lang=</c> query and the default.</param>
public sealed class ResidentLanguageFilter(bool welcome) : IAsyncPageFilter, IAsyncResultFilter
{
    private const string ItemKey = "dg-lang";

    /// <summary>The language picked for this request; English when no resident filter ran.</summary>
    public static string Current(HttpContext ctx) => Picked(ctx) ?? Core.Languages.English;

    /// <summary>The language picked for this request, or null when no resident filter ran.</summary>
    internal static string? Picked(HttpContext ctx) => ctx.Items[ItemKey] as string;

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var culture = await Pick(context.HttpContext);
        // Set here, not in Pick: a culture changed inside an awaited async method is dropped when it returns (AsyncLocal).
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        await next();
    }

    // The page is rendered after the handler, in the result stage, which the page filter does not wrap; the culture is set there too.
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var culture = await Pick(context.HttpContext);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        await next();
    }

    private async Task<CultureInfo> Pick(HttpContext ctx)
    {
        if (!ctx.Items.ContainsKey(ItemKey))
        {
            var sp = ctx.RequestServices;
            // Asked of the guest cookie itself: pages such as /home and /signup are not under the guest policy, so User is not the guest.
            var guest = welcome ? null : (await ctx.AuthenticateAsync(AuthSchemes.Guest)).Principal;
            ctx.Items[ItemKey] = LanguagePick.For(ctx, sp.GetRequiredService<Func<LauncherSettings>>()().Languages,
                sp.GetRequiredService<ResidentLanguages>(), guest, welcome);
        }
        return Core.Languages.Culture(Current(ctx));
    }
}
