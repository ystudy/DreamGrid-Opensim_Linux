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
/// internet and then goes like Local or like Simple Public. The save goes through IGridController.UpdateSettings.
/// </summary>
public sealed class NetworkModel(IGridController grid, Func<LauncherSettings> settings) : PageModel
{
    /// <summary>"lan" or "public" (Grid Asset Server only; Simple Public is always public, Local always lan).</summary>
    [BindProperty] public string? AddressKind { get; set; }
    [BindProperty] public string? PublicAddress { get; set; }

    public OwnerPlan Plan { get; private set; }
    public string DetectedAddress { get; } = HostAddress.Detect();
    public string? AddressError { get; private set; }

    /// <summary>The network address the world will use: Local's kept custom hostname, otherwise the detected one.</summary>
    public string NetworkAddress { get; private set; } = "";

    /// <summary>The ports to forward, in plain words, from the current settings.</summary>
    public string Ports { get; private set; } = "";
    public HypergridSwitchModel Switch { get; private set; } = new(false, true, null, 0, 0);

    /// <summary>The advisories for a network-only address; empty for Simple Public, which is always public.</summary>
    public IReadOnlyList<string> LanAdvisories { get; private set; } = [];

    /// <summary>The advisories for a public address; empty for Local, which is always network-only.</summary>
    public IReadOnlyList<string> PublicAdvisories { get; private set; } = [];

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        var s = settings();
        Plan = s.Plan!.Value;
        var isPublic = Plan != OwnerPlan.Local && HostAddress.IsPublic(s.Hostname);
        AddressKind = isPublic && Plan == OwnerPlan.GridAsset ? "public" : "lan";
        PublicAddress = isPublic ? s.Hostname : null;
        Fill(s, hypergridOn: s.Hypergrid && isPublic);
        return Page();
    }

    public IActionResult OnPost()
    {
        if (Closed() is { } closed) return closed;
        var s = settings();
        Plan = s.Plan!.Value;
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

    private string? KeptLocalHostname(LauncherSettings s) =>
        Plan == OwnerPlan.Local && !string.IsNullOrEmpty(s.Hostname) && PlanCatalog.AddressError(OwnerPlan.Local, s.Hostname) is null
            ? s.Hostname
            : null;

    private static bool IsOn(string value) => value != "" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    private IActionResult? Closed() => SetupSteps.CanOpen(settings(), "/setup/network") ? null : Redirect("/setup");
}
