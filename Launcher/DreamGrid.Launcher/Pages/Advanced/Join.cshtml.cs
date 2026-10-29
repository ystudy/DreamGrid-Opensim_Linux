using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>
/// A finished Region Server's Grid Asset Server: the same read, show and confirm as the setup join step
/// (<see cref="JoinForm"/>), but the confirm rewrites the INIs and asks for a restart. Any other plan, or a setup not
/// finished, goes back to Advanced settings. Nothing is saved while the address or the read fails.
/// </summary>
public sealed class JoinModel(IGridController grid, Func<LauncherSettings> settings, IGridJoinClient join) : PageModel
{
    [BindProperty] public string? Address { get; set; }

    /// <summary>The join information the first post read, shown with "Join this grid"; null shows the address form.</summary>
    public JoinedGrid? Found { get; private set; }

    /// <summary>Why the address can't be used or the join information couldn't be read, for the owner.</summary>
    public string? Error { get; private set; }

    /// <summary>The Grid Asset Server this computer uses now, as the owner would type it.</summary>
    public string Current { get; private set; } = "";

    /// <summary>Set after a save: the "restart your world" notice (no extra notes).</summary>
    public IReadOnlyList<string>? SavedNotes { get; private set; }

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        Address = Current;
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

        // The estate owner's account id belongs to the old grid: save the new join, then look the owner up on the
        // new grid (which saves the new id). If that fails the old join is put back.
        var before = settings();
        grid.UpdateSettings(s => JoinForm.Apply(s, read), writeConfig: true);
        if (before.Setup.Owner is { } owner)
        {
            try
            {
                await grid.SetEstateOwnerAsync(owner.FirstName, owner.LastName, HttpContext.RequestAborted);
            }
            catch (Exception e) when (e is ArgumentException or GridAssetUnreachableException)
            {
                grid.UpdateSettings(s => s with { Join = before.Join, GridName = before.GridName }, writeConfig: true);
                Error = e is ArgumentException ? e.Message + " Create your avatar on that grid first, then try again." : e.Message;
                Found = read;
                Address = JoinForm.AddressOf(read);
                return Page();
            }
        }
        Current = JoinForm.AddressOf(read);
        SavedNotes = [];
        return Page();
    }

    private IActionResult? Closed()
    {
        var s = settings();
        if (s.Plan != OwnerPlan.RegionServer || !s.Setup.Finished) return Redirect("/advanced");
        Current = s.Join is { } joined ? JoinForm.AddressOf(joined) : "";
        return null;
    }
}
