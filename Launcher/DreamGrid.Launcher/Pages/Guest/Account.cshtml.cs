using System.Globalization;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// A signed-in person's own account (GuestPolicy, by the /Guest folder convention; behind <see cref="TermsGate"/>), for residents and
/// for staff at the guest door (spec 2026-10-31-resident-account-design §3–§6): their details, change password, and change email.
/// The work is <see cref="AccountChanges"/>'s; this page shows the outcome. A changed password signs this browser in again with
/// the new stamp, so it stays signed in while every other session ends.
/// </summary>
public sealed class AccountModel(AccountChanges changes, IRobustClient robust, Func<LauncherSettings> settings, RegionRights rights,
    ResidentTwoFactor residents, ILogger<AccountModel> log) : PageModel
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
        var r = await changes.ChangePasswordAsync(guest.Principal, Current, Code, Password, Again, HttpContext.Connection.RemoteIpAddress, ct);
        await LoadAsync(guest.Principal, ct);
        if (r.Result == AccountResult.Done)
        {
            // The stamp just changed, so this browser's cookie is no longer good: issue a new one.
            if (Account is not null) await GuestSignIn.SignInAsync(HttpContext, Account);
            PasswordNotice = AccountChanges.PasswordDone;
        }
        else PasswordError = r.Problem;
        return Page();
    }

    public async Task<IActionResult> OnPostEmailAsync(CancellationToken ct)
    {
        if (GuestSignIn.Read(User) is not { } guest) return Challenge();
        var r = await changes.AskEmailChangeAsync(guest.Principal, Current, Code, NewEmail, HttpContext.Connection.RemoteIpAddress, ct);
        await LoadAsync(guest.Principal, ct);
        if (r.Result == AccountResult.Done)
        {
            EmailNotice = AccountChanges.AskReply;
            NewEmail = null;
        }
        else EmailError = r.Problem;
        return Page();
    }

    private async Task LoadAsync(Guid principal, CancellationToken ct)
    {
        var s = settings();
        try
        {
            Account = await robust.FindAccountByIdAsync(principal, ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or InvalidDataException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Account page: the account service did not answer: {Message}", e.Message);
        }
        if (Account is null)
        {
            Unavailable = AccountChanges.ServiceDown;
            return;
        }
        Role = AdminRule.RoleOf(s, Account) switch
        {
            StaffRole.Owner => "Owner",
            StaffRole.Admin => "Admin",
            StaffRole.Supervisor => "Supervisor",
            _ => "Resident",
        };
        Created = Account.Created?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        NeedsCode = changes.NeedsCode(principal);
        EmailChangeAvailable = changes.EmailChangeAvailable();
        TwoFactorLink = ResidentTwoFactor.PageShown(s, residents.Store.StateOf(principal));
        OwnsRegions = rights.OwnedBy(principal).Count > 0;
    }
}
