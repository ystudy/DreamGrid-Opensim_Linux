using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// Where the change-email link leads (anonymous, outside <see cref="TermsGate"/>: people often open email on another device). A live
/// link writes the new address (<see cref="AccountChanges.ConfirmEmailChangeAsync"/>); a used, expired or wrong one says so; an
/// address another avatar already has is refused; when the grid doesn't answer, it says to try again, and the link stays good.
/// </summary>
public sealed class ConfirmEmailModel(AccountChanges changes) : PageModel
{
    public EmailLinkResult Result { get; private set; }
    public string? Email { get; private set; }

    public async Task OnGetAsync(string? token, CancellationToken ct) => (Result, Email) = await changes.ConfirmEmailChangeAsync(token, ct);
}
