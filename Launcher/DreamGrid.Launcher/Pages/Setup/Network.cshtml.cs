using System.Globalization;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>
/// The network step. Local keeps the detected network address and has no hypergrid. Simple Public Server needs a public
/// address and offers the hypergrid switch (a custom network hostname Local already has is kept). Grid Asset Server asks first whether visitors are on the network or the
/// internet and then goes like Local or like Simple Public. A Region Server asks for this computer's address (on the
/// network or from the internet) and its first region port, and has no hypergrid switch: the Grid Asset Server decides.
/// A Connected Server asks the same, starting at the internet choice, and the other grid decides about hypergrid.
/// The save goes through IGridController.UpdateSettings.
/// </summary>
public sealed class NetworkModel(IGridController grid, Func<LauncherSettings> settings) : PageModel
{
    /// <summary>"lan" or "public" (Grid Asset Server and Region Server; Simple Public is always public, Local always lan).</summary>
    [BindProperty] public string? AddressKind { get; set; }
    [BindProperty] public string? PublicAddress { get; set; }

    /// <summary>A Region Server's first region port, as typed (saved as RegionPort).</summary>
    [BindProperty] public string? FirstRegionPort { get; set; }

    /// <summary>Why <see cref="FirstRegionPort"/> can't be used (Region Server only).</summary>
    public string? PortError { get; private set; }

    /// <summary>A Region Server's one hypergrid sentence: the joined grid's setting, which the Grid Asset Server decides.</summary>
    public string HypergridLine { get; private set; } = "";

    /// <summary>The region ports the Grid Asset Server reports for its own regions, in plain words; null when it reports none.</summary>
    public string? GridAssetRegionPorts { get; private set; }

    public OwnerPlan Plan { get; private set; }
    public string DetectedAddress { get; } = HostAddress.Detect();
    public string? AddressError { get; private set; }

    /// <summary>The network address the world will use: Local's kept custom hostname, otherwise the detected one.</summary>
    public string NetworkAddress { get; private set; } = "";

    /// <summary>The ports to forward, in plain words, from the current settings.</summary>
    public string Ports { get; private set; } = "";
    public HypergridSwitchModel Switch { get; private set; } = new(false, true, null, 0, 0);

    /// <summary>The advisories for a network-only address; empty for Simple Public, which is always public. A Region Server's whole list.</summary>
    public IReadOnlyList<string> LanAdvisories { get; private set; } = [];

    /// <summary>The advisories for a public address; empty for Local, which is always network-only.</summary>
    public IReadOnlyList<string> PublicAdvisories { get; private set; } = [];

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        var s = settings();
        Plan = s.Plan!.Value;
        var isPublic = Plan != OwnerPlan.Local && HostAddress.IsPublic(s.Hostname);
        // Regions on someone else's grid are reached from the internet, so a Connected Server starts at that choice.
        AddressKind = Plan == OwnerPlan.Connected || (isPublic && Plan is OwnerPlan.GridAsset or OwnerPlan.RegionServer) ? "public" : "lan";
        PublicAddress = isPublic ? s.Hostname : null;
        if (Plan is OwnerPlan.RegionServer or OwnerPlan.Connected)
        {
            FirstRegionPort = SuggestedRegionPort(s).ToString(CultureInfo.InvariantCulture);
            FillRegionServer(s);
            return Page();
        }
        Fill(s, hypergridOn: s.Hypergrid && isPublic);
        return Page();
    }

    /// <summary>
    /// The "Find my address" button: this computer's public IP as the internet sees it (not the browser's), as
    /// <c>{ address }</c> or <c>{ error }</c>. Saves nothing; the owner still presses Continue.
    /// </summary>
    public async Task<IActionResult> OnGetPublicAddressAsync([FromServices] IPublicAddressFinder finder, CancellationToken ct)
    {
        if (Closed() is { } closed) return closed;
        return await finder.FindAsync(ct) is { } address
            ? new JsonResult(new { address })
            : new JsonResult(new { error = NoPublicAddress });
    }

    public const string NoPublicAddress = "DreamGrid couldn't find this computer's public address. Check its internet connection, or type the address.";

    public IActionResult OnPost()
    {
        if (Closed() is { } closed) return closed;
        var s = settings();
        Plan = s.Plan!.Value;
        if (Plan is OwnerPlan.RegionServer or OwnerPlan.Connected) return PostRegionServer(s);
        var wantsHypergrid = IsOn(Request.Form["Hypergrid"].ToString());

        // Local is always the network address; Simple Public always public; Grid Asset follows the radio (network by default).
        var isPublic = Plan switch
        {
            OwnerPlan.Local => false,
            OwnerPlan.SimplePublic => true,
            _ => AddressKind == "public",
        };
        AddressKind = isPublic && Plan == OwnerPlan.GridAsset ? "public" : "lan";

        // A network-only Grid Asset Server uses the detected address; Local keeps a custom hostname when it is on the
        // network (PlanCatalog.AddressError refuses a public one for Local).
        string? hostname = KeptLocalHostname(s);
        if (isPublic)
        {
            hostname = PublicAddress?.Trim() is { Length: > 0 } typed ? typed : null;
            // A public address is checked by Simple Public Server's rule, whichever plan asked for it.
            AddressError = PlanCatalog.AddressError(OwnerPlan.SimplePublic, hostname);
            if (AddressError is not null)
            {
                Fill(s, wantsHypergrid);
                return Page();
            }
        }

        var hypergrid = isPublic && PlanCatalog.Get(Plan).Hypergrid == HypergridCapability.Switch && wantsHypergrid;
        grid.UpdateSettings(x => x with { Hostname = hostname, Hypergrid = hypergrid, Setup = x.Setup with { NetworkSet = true } }, writeConfig: true);
        return Redirect("/setup");
    }

    private void Fill(LauncherSettings s, bool hypergridOn)
    {
        var info = PlanCatalog.Get(Plan);
        NetworkAddress = KeptLocalHostname(s) ?? DetectedAddress;
        LanAdvisories = Plan == OwnerPlan.SimplePublic ? [] : PlanCatalog.AdvisoriesFor(info, publicAddress: false);
        PublicAdvisories = Plan == OwnerPlan.Local ? [] : PlanCatalog.AdvisoriesFor(info, publicAddress: true);
        Ports = string.Create(CultureInfo.InvariantCulture, $"{s.RobustPublicPort}, and {s.RegionPort} upward for regions");
        Switch = new HypergridSwitchModel(hypergridOn, true, null, s.RobustPublicPort, s.RegionPort);
    }

    /// <summary>
    /// A Region Server's save: the address (null for the detected network address) and the first region port. The
    /// hypergrid setting is left alone: a Region Server follows the grid it joined.
    /// </summary>
    private IActionResult PostRegionServer(LauncherSettings s)
    {
        var isPublic = AddressKind == "public";
        AddressKind = isPublic ? "public" : "lan";
        string? hostname = null;
        if (isPublic)
        {
            hostname = PublicAddress?.Trim() is { Length: > 0 } typed ? typed : null;
            AddressError = PlanCatalog.AddressError(OwnerPlan.SimplePublic, hostname);
        }
        PortError = RegionPortError(s, FirstRegionPort, out var port);
        if (AddressError is not null || PortError is not null)
        {
            FillRegionServer(s);
            return Page();
        }

        grid.UpdateSettings(x => x with { Hostname = hostname, RegionPort = port, Setup = x.Setup with { NetworkSet = true } }, writeConfig: true);
        return Redirect("/setup");
    }

    /// <summary>
    /// The first region port to offer: the one already saved once this step is done, else the port after the Grid Asset
    /// Server's own region ports when it reports them, else this computer's first region port.
    /// </summary>
    private static int SuggestedRegionPort(LauncherSettings s) =>
        !s.Setup.NetworkSet && s.Join?.RegionPortsTo is { } to && to < SettingsValidator.MaxPort ? to + 1 : s.RegionPort;

    /// <summary>Plain words when <paramref name="text"/> can't be the first region port: not a port, or one this computer's other parts use.</summary>
    private static string? RegionPortError(LauncherSettings s, string? text, out int port)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < SettingsValidator.MinPort or > SettingsValidator.MaxPort)
            return "Enter a whole number from 1024 to 65535.";
        int[] used = [s.WebPort, s.MariaDbPort, s.RegionConsolePort, s.RobustPublicPort, s.RobustPrivatePort, s.RobustConsolePort];
        return used.Contains(port) ? $"Port {port} is already used by another part of DreamGrid on this computer. Pick a different number." : null;
    }

    private void FillRegionServer(LauncherSettings s)
    {
        NetworkAddress = DetectedAddress;
        LanAdvisories = PlanCatalog.AdvisoriesFor(PlanCatalog.Get(Plan), publicAddress: AddressKind == "public");
        PublicAdvisories = [];
        HypergridLine = PlanCatalog.OtherGridHypergridLine(s) ?? "";
        GridAssetRegionPorts = s.Join is { RegionPortsFrom: { } from, RegionPortsTo: { } to }
            ? string.Create(CultureInfo.InvariantCulture, $"{from} to {to}")
            : null;
    }

    private string? KeptLocalHostname(LauncherSettings s) =>
        Plan == OwnerPlan.Local && !string.IsNullOrEmpty(s.Hostname) && PlanCatalog.AddressError(OwnerPlan.Local, s.Hostname) is null
            ? s.Hostname
            : null;

    private static bool IsOn(string value) => value != "" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    private IActionResult? Closed() => SetupSteps.CanOpen(settings(), "/setup/network") ? null : Redirect("/setup");
}
