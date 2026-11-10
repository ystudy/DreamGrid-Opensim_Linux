using System.Globalization;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using DreamGrid.Launcher.Resident;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// One of the operator's legal pages, at /terms, /privacy, /cookies and /contact. Open to anyone (<c>AdminNetworkFilter</c> lets
/// it through from any address). It answers 404 until the operator has written the page, and, with the other guest pages, on a
/// Region Server or a Connected Server (<c>GuestSideOnly</c>).
/// </summary>
public sealed class LegalModel(Func<LauncherSettings> settings, IStringLocalizer<ResidentText> t) : PageModel
{
    public string PageTitle { get; private set; } = "";
    public int Version { get; private set; }
    public string Saved { get; private set; } = "";

    /// <summary>"This page is shown in {0}." when the text is not in the reader's language (the grid's default or English is shown); else null.</summary>
    public string? ShownIn { get; private set; }

    /// <summary>The operator's text as safe HTML (<see cref="LegalMarkdown"/>).</summary>
    public string Html { get; private set; } = "";

    public IActionResult OnGet(string slug)
    {
        slug = slug.ToLowerInvariant(); // the route constraint ignores case: /Terms reaches here as "Terms"
        var language = ResidentLanguageFilter.Current(HttpContext);
        if (LegalPages.Find(settings(), slug, language) is not var (text, shown)) return NotFound();
        PageTitle = t.LegalTitle(slug);
        Version = text.Version;
        Saved = text.Saved.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        if (shown != language) ShownIn = t["Legal.ShownInDefault", Core.Languages.NativeName(shown)];
        Html = LegalMarkdown.ToHtml(text.Markdown);
        return Page();
    }
}
