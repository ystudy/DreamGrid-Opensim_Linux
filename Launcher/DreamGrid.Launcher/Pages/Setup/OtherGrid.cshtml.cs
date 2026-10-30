using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>
/// A Connected Server's step that connects to someone else's grid: OSgrid by one choice, or another grid by its login
/// address and its address for region servers. The first post reads the grid's information and shows it;
/// <c>?handler=Confirm</c> reads it again, never trusting posted values for what the grid publishes, and saves it (with the
/// grid's name, which stands in for naming the world) through IGridController.UpdateSettings. Nothing is saved while an
/// address or a read fails.
/// </summary>
public sealed class OtherGridModel(IGridController grid, Func<LauncherSettings> settings, IOtherGridClient other) : PageModel
{
    /// <summary>The Grid choice that means "type the addresses"; any other value is a preset's id.</summary>
    public const string AnotherGrid = "other";

    /// <summary>A preset's id (OSgrid), or <see cref="AnotherGrid"/>.</summary>
    [BindProperty] public string? Grid { get; set; } = GridPresets.All[0].Id;
    [BindProperty] public string? LoginAddress { get; set; }
    [BindProperty] public string? ServicesAddress { get; set; }

    /// <summary>What the first post read, shown with "Connect to this grid"; null shows the form.</summary>
    public OtherGridInfo? Found { get; private set; }

    /// <summary>The login address that was read, for the panel that shows what was found.</summary>
    public Uri? FoundAt { get; private set; }

    /// <summary>Why a typed address can't be used, shown beside its field.</summary>
    public string? LoginError { get; private set; }
    public string? ServicesError { get; private set; }

    /// <summary>Why the grid couldn't be read, for the owner.</summary>
    public string? Error { get; private set; }

    public bool IsAnotherGrid => Grid == AnotherGrid;

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        if (settings().Connected is { } connected)
        {
            Grid = connected.Preset ?? AnotherGrid;
            if (connected.Preset is null)
                (LoginAddress, ServicesAddress) = (connected.Address, connected.ServicesUri);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Closed() is { } closed) return closed;
        if (await ReadAsync() is { } read)
            (Found, FoundAt) = (read.Info, read.Address);
        return Page();
    }

    /// <summary>"Change": the form again, with what was typed.</summary>
    public IActionResult OnPostChange() => Closed() ?? Page();

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        if (Closed() is { } closed) return closed;
        if (await ReadAsync() is not { } read) return Page();

        var connected = new ConnectedGrid(read.Preset?.Id, read.Info.GridName, read.Address.ToString(), read.Info.LoginUri,
            read.Services.ToString(), read.Info.GatekeeperUri);
        grid.UpdateSettings(s => s with
        {
            Connected = connected,
            GridName = read.Info.GridName,
            Setup = s.Setup with { WorldNamed = true },
        }, writeConfig: true);
        return Redirect("/setup");
    }

    private sealed record Read(GridPreset? Preset, Uri Address, Uri Services, OtherGridInfo Info);

    /// <summary>
    /// The chosen grid's information, or null with the owner's message in <see cref="Error"/>, <see cref="LoginError"/> or
    /// <see cref="ServicesError"/>: a mistyped address, or the <see cref="GridJoinException"/> a read gave.
    /// </summary>
    private async Task<Read?> ReadAsync()
    {
        var ct = HttpContext.RequestAborted;
        GridPreset? preset = null;
        Uri? address, services;
        if (IsAnotherGrid)
        {
            address = OtherGridAddress.Parse(LoginAddress, OtherGridAddress.LoginPort, out var loginError);
            services = OtherGridAddress.Parse(ServicesAddress, OtherGridAddress.ServicesPort, out var servicesError);
            (LoginError, ServicesError) = (loginError, servicesError);
        }
        else if (GridPresets.ById(Grid) is { } chosen)
        {
            preset = chosen;
            (address, services) = (new Uri(chosen.Address), new Uri(chosen.GridUri));
        }
        else
        {
            Error = "Choose the grid to connect to.";
            return null;
        }
        if (address is null || services is null) return null;

        try
        {
            var info = await other.ReadAsync(address, ct);
            await other.CheckServicesAsync(services, ct);
            return new Read(preset, address, services, info);
        }
        catch (GridJoinException e)
        {
            Error = e.Message;
            return null;
        }
    }

    // Once the owner is named on the other grid, this step is closed.
    private IActionResult? Closed()
    {
        var s = settings();
        return s.Setup.OwnerCreated || !SetupSteps.CanOpen(s, SetupSteps.OtherGridPath) ? Redirect("/setup") : null;
    }
}
