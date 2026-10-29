using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>
/// A Region Server's join step: the Grid Asset Server's address. The first post reads its join information and shows it;
/// <c>?handler=Confirm</c> reads it again and saves it (with the grid's name, which stands in for naming the world) through
/// IGridController.UpdateSettings, then warms the grid up. Nothing is saved while the address or the read fails.
/// </summary>
public sealed class JoinModel(IGridController grid, Func<LauncherSettings> settings, IGridJoinClient join, SetupRunner runner) : PageModel
{
    [BindProperty] public string? Address { get; set; }

    /// <summary>The join information the first post read, shown with "Join this grid"; null shows the address form.</summary>
    public JoinedGrid? Found { get; private set; }

    /// <summary>Why the address can't be used or the join information couldn't be read, for the owner.</summary>
    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        Address = settings().Join is { } joined ? JoinForm.AddressOf(joined) : null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Closed() is { } closed) return closed;
        (Found, Error) = await JoinForm.ReadAsync(join, Address, HttpContext.RequestAborted);
        if (Found is not null) Address = JoinForm.AddressOf(Found);
        return Page();
    }

    /// <summary>"Change the address": the form again, with the address filled in.</summary>
    public IActionResult OnPostChange() => Closed() ?? Page();

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        if (Closed() is { } closed) return closed;
        var (read, error) = await JoinForm.ReadAsync(join, Address, HttpContext.RequestAborted);
        if (read is null)
        {
            Error = error;
            return Page();
        }

        grid.UpdateSettings(s => JoinForm.Apply(s, read) with { Setup = s.Setup with { WorldNamed = true } }, writeConfig: true);
        runner.WarmUp();
        return Redirect("/setup");
    }

    // Once the owner is named on the joined grid, the grid is changed from Advanced settings, not here.
    private IActionResult? Closed()
    {
        var s = settings();
        return s.Setup.OwnerCreated || !SetupSteps.CanOpen(s, SetupSteps.JoinPath) ? Redirect("/setup") : null;
    }
}
