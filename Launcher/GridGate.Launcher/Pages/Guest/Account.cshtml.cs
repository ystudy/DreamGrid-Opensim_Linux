using GridGate.Launcher.Email;
using System.Globalization;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Resident;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages.Guest;

/// <summary>
/// A signed-in person's own account (GuestPolicy, by the /Guest folder convention; behind <see cref="TermsGate"/>), for residents and
/// for staff at the guest door (spec 2026-10-31-resident-account-design §3–§6): their details, change password, and change email.
/// The work is <see cref="AccountChanges"/>'s; this page shows the outcome. A changed password signs this browser in again with
/// the new stamp, so it stays signed in while every other session ends.
/// </summary>
public sealed class AccountModel(AccountChanges changes, IRobustClient robust, Func<LauncherSettings> settings, RegionRights rights,
    ResidentTwoFactor residents, EmailLanguage emailLanguage, OfferedLanguages offeredLanguages, ResidentLanguages languages, ILogger<AccountModel> log, IStringLocalizer<ResidentText> t) : PageModel
{
    [BindProperty] public string? Current { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Again { get; set; }
    [BindProperty] public string? NewEmail { get; set; }

    public RobustAccount? Account { get; private set; }
    public string Role { get; private set; } = "";
    public string? Created { get; private set; }
    public bool NeedsCode { get; private set; }
    public bool EmailChangeAvailable { get; private set; }
    public bool TwoFactorLink { get; private set; }
    public bool OwnsRegions { get; private set; }

    /// <summary>The languages to choose from, as code and the language's own name.</summary>
    public IReadOnlyList<(string Code, string Name)> LanguageChoices { get; private set; } = [];

    /// <summary>The language shown selected: the one saved for this account, else the one this page is in.</summary>
    public string SelectedLanguage { get; private set; } = Core.Languages.English;

    /// <summary>Shown when the account can't be read: no forms then.</summary>
    public string? Unavailable { get; private set; }

    public string? PasswordError { get; private set; }
    public string? PasswordNotice { get; private set; }
    public string? EmailError { get; private set; }
    public string? EmailNotice { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (GuestSignIn.Read(User) is not { } guest) return Challenge();
        await LoadAsync(guest.Principal, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync(CancellationToken ct)
    {
        if (GuestSignIn.Read(User) is not { } guest) return Challenge();
        var r = await changes.ChangePasswordAsync(guest.Principal, Current, Code, Password, Again, HttpContext.Connection.RemoteIpAddress, ct,
            emailLanguage.ForRequest(HttpContext));
        await LoadAsync(guest.Principal, ct);
        if (r.Result == AccountResult.Done)
        {
            // The stamp just changed, so this browser's cookie is no longer good: issue a new one.
            if (Account is not null) await GuestSignIn.SignInAsync(HttpContext, Account);
            PasswordNotice = t["Account.PasswordDone"];
        }
        else PasswordError = r.Message!.In(t);
        return Page();
    }

    public async Task<IActionResult> OnPostEmailAsync(CancellationToken ct)
    {
        if (GuestSignIn.Read(User) is not { } guest) return Challenge();
        var r = await changes.AskEmailChangeAsync(guest.Principal, Current, Code, NewEmail, HttpContext.Connection.RemoteIpAddress, ct,
            emailLanguage.ForRequest(HttpContext));
        await LoadAsync(guest.Principal, ct);
        if (r.Result == AccountResult.Done)
        {
            EmailNotice = t["Account.AskReply"];
            NewEmail = null;
        }
        else EmailError = r.Message!.In(t);
        return Page();
    }

    public Task<IActionResult> OnPostLanguageAsync(string? language)
    {
        if (GuestSignIn.Read(User) is not { } guest) return Task.FromResult<IActionResult>(Challenge());
        var offered = offeredLanguages.Current();
        if (Core.Languages.Match(language, offered) is not { } chosen) return Task.FromResult<IActionResult>(BadRequest());
        languages.Set(guest.Principal, chosen);
        LanguagePick.SetCookie(HttpContext, chosen);
        // Back to the page, so it is drawn in the new language (this request's page was picked before the choice).
        return Task.FromResult<IActionResult>(RedirectToPage());
    }

    private async Task LoadAsync(Guid principal, CancellationToken ct)
    {
        var s = settings();
        try
        {
            Account = await robust.FindAccountByIdAsync(principal, ct);
        }
        catch (Exception e) when (RobustFailure.IsUnreachable(e, ct))
        {
            log.LogWarning("Account page: the account service did not answer: {Message}", e.Message);
        }
        if (Account is null)
        {
            Unavailable = t["Common.ServiceDown"];
            return;
        }
        Role = AdminRule.RoleOf(s, Account) switch
        {
            StaffRole.Owner => t["Account.RoleOwner"].Value,
            StaffRole.Admin => t["Account.RoleAdmin"].Value,
            StaffRole.Supervisor => t["Account.RoleSupervisor"].Value,
            _ => t["Account.RoleResident"].Value,
        };
        Created = Account.Created?.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        NeedsCode = changes.NeedsCode(principal);
        EmailChangeAvailable = changes.EmailChangeAvailable();
        TwoFactorLink = ResidentTwoFactor.PageShown(s, residents.Store.StateOf(principal));
        OwnsRegions = rights.OwnedBy(principal).Count > 0;
        var offered = offeredLanguages.Current();
        LanguageChoices = [.. offered.Select(c => (c, Core.Languages.NativeName(c)))];
        SelectedLanguage = Core.Languages.Match(languages.Get(principal), offered) ?? ResidentLanguageFilter.Current(HttpContext);
    }
}
