using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// The operator's current Terms for a signed-in guest to read and accept (<see cref="TermsGate"/> sends them here). The form
/// names the version it showed; if the Terms changed meanwhile the acceptance is refused and the new version is shown. With no
/// Terms text there is nothing to accept and the guest goes on.
/// </summary>
public sealed class TermsModel(Func<LauncherSettings> settings, TermsAcceptances acceptances, AuditLog audit,
    IStringLocalizer<ResidentText> t) : PageModel
{
    public string GridName { get; private set; } = "";
    public int Version { get; private set; }

    /// <summary>The operator's text as safe HTML (<see cref="LegalMarkdown"/>).</summary>
    public string Html { get; private set; } = "";

    public bool Accepted { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Where to go after accepting: the page the guest came from, when it is on this site.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The ReturnUrl when it is on this site, else the guest's home.</summary>
    public string Next => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/guest";

    public IActionResult OnGet()
    {
        if (LegalPages.Get(settings(), LegalPages.Terms, settings().Languages.Default) is not { } terms) return Redirect(Next);
        Show(terms);
        return Page();
    }

    public IActionResult OnPost([FromForm(Name = "Version")] int version)
    {
        if (LegalPages.Get(settings(), LegalPages.Terms, settings().Languages.Default) is not { } terms) return Redirect(Next);
        if (version != terms.Version)
        {
            Show(terms);
            Error = t["Terms.Changed"];
            return Page();
        }
        var guest = Guest();
        if (!acceptances.HasAccepted(guest.Principal, terms.Version))
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            acceptances.Accept(guest.Principal, terms.Version, ip);
            audit.Write(new AuditEntry(User.Identity?.Name ?? "", guest.Principal, AuthSchemes.GuestRole, ip,
                "terms-accepted", "terms v" + terms.Version, "ok"));
        }
        return Redirect(Next);
    }

    /// <summary>The guest's claims; the page is behind the guest policy, so they are there.</summary>
    private (Guid Principal, string Stamp, DateTimeOffset Checked) Guest() => GuestSignIn.Read(User)!.Value;

    private void Show(LegalText terms)
    {
        GridName = settings().GridName;
        Version = terms.Version;
        Html = LegalMarkdown.ToHtml(terms.Markdown);
        Accepted = acceptances.HasAccepted(Guest().Principal, terms.Version);
    }
}
