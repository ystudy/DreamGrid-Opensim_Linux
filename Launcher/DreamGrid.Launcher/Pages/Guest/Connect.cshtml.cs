using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Resident;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// How to connect with a viewer (spec 2026-10-31-sign-up-design §6, looks from the 2026-11-05 connect page mockup): viewer
/// links, the grid manager settings to copy, the Firestorm steps and help links. Open to anyone, since nothing on it is a
/// secret; a signed-in resident is told which name to log in as.
/// </summary>
public sealed class ConnectModel(Func<LauncherSettings> settings, IStringLocalizer<ResidentText> t) : PageModel
{
    /// <summary>A viewer and its own site. The grid hosts no viewers and pins no versions, which would go out of date.</summary>
    public sealed record Viewer(string Name, string Url);

    /// <summary>Firestorm's downloads page, where people pick their system and the OpenSim build.</summary>
    public static readonly Viewer Firestorm = new("Firestorm", "https://www.firestormviewer.org/downloads/");

    public static readonly IReadOnlyList<Viewer> Others =
    [
        new("Singularity", "https://www.singularityviewer.org/"),
        new("Cool VL Viewer", "https://sldev.pages-perso.free.fr/"),
        new("Alchemy", "https://www.alchemyviewer.org/"),
    ];

    public const string FirestormHelp = "https://wiki.firestormviewer.org/";

    public string GridName { get; private set; } = "";
    public string LoginUri { get; private set; } = "";
    public string DisplayName { get; private set; } = "";

    /// <summary>The grid manager settings, as label and value, in the order viewers ask for them.</summary>
    public IReadOnlyList<(string Label, string Value)> GridSettings { get; private set; } = [];

    /// <summary>The legal pages the operator has written that belong in Help (Terms and Contact), as id and title.</summary>
    public IReadOnlyList<(string Id, string Title)> HelpPages { get; private set; } = [];

    /// <summary>Arrived from the first sign-in after a staff approval.</summary>
    public bool Approved { get; private set; }

    public void OnGet(int? approved)
    {
        var s = settings();
        GridName = s.GridName;
        LoginUri = GridEndpoints.LoginUri(s, HostAddress.Effective(s.Hostname));
        DisplayName = GuestSignIn.Read(User) is not null ? User.Identity?.Name ?? "" : "";
        Approved = approved == 1 && DisplayName.Length > 0;
        GridSettings =
        [
            (t["Connect.LoginUri"].Value, LoginUri),
            (t["Connect.GridName"].Value, s.GridName),
            (t["Connect.LoginPage"].Value, WebsiteAddress.Welcome(s)),
            (t["Connect.Website"].Value, WebsiteAddress.Base(s) + "/"),
            (t["Connect.Account"].Value, WebsiteAddress.SignUp(s)),
            (t["Connect.Password"].Value, WebsiteAddress.ForgotPassword(s)),
        ];
        HelpPages = new[] { LegalPages.Terms, LegalPages.Contact }
            .Where(id => LegalPages.Get(s, id, ResidentLanguageFilter.Current(HttpContext)) is not null)
            .Select(id => (id, LegalPages.Title(id)))
            .ToList();
    }

    /// <summary>The settings as one block of text, with the labels lined up, for the Copy button and the page.</summary>
    public string SettingsText()
    {
        var width = GridSettings.Max(g => g.Label.Length) + 2;
        return string.Join('\n', GridSettings.Select(g => (g.Label + ":").PadRight(width) + g.Value));
    }
}
