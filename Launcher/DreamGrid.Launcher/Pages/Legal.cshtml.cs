using System.Globalization;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// One of the operator's legal pages, at /terms, /privacy, /cookies and /contact. Open to anyone (<c>AdminNetworkFilter</c> lets
/// it through from any address). It answers 404 until the operator has written the page, and, with the other guest pages, on a
/// Region Server or a Connected Server (<c>GuestSideOnly</c>).
/// </summary>
public sealed class LegalModel(Func<LauncherSettings> settings) : PageModel
{
    public string PageTitle { get; private set; } = "";
    public int Version { get; private set; }
    public string Saved { get; private set; } = "";

    /// <summary>The operator's text as safe HTML (<see cref="LegalMarkdown"/>).</summary>
    public string Html { get; private set; } = "";

    public IActionResult OnGet(string slug)
    {
        var text = LegalPages.Get(settings(), slug);
        if (text is null) return NotFound();
        PageTitle = LegalPages.Title(slug);
        Version = text.Version;
        Saved = text.Saved.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        Html = LegalMarkdown.ToHtml(text.Markdown);
        return Page();
    }
}
