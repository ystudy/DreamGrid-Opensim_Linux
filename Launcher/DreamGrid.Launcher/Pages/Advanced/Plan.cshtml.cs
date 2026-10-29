using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>
/// The owner's plan after setup: what it is, which other plans it can change to (every change is checked against
/// <see cref="PlanCatalog.CanChange"/> here, whatever the page offered), and the hypergrid switch where the plan has one.
/// Saves go through IGridController.UpdateSettings, which also rewrites the INIs.
/// </summary>
public sealed class PlanModel(IGridController grid, Func<LauncherSettings> settings) : PageModel
{
    /// <summary>The note the network page shows after a change to a public plan without a public address.</summary>
    public const string FinishNote = "Enter your public address to finish the change.";

    public PlanInfo Current { get; private set; } = PlanCatalog.Get(OwnerPlan.Local);

    /// <summary>The current plan's advisories for the address it has now.</summary>
    public IReadOnlyList<string> Advisories { get; private set; } = [];

    /// <summary>The other plans: where this plan may go, and where it may not (with the reason).</summary>
    public IReadOnlyList<(PlanInfo Plan, bool Allowed, string Reason)> Changes { get; private set; } = [];

    /// <summary>Null when this plan has no hypergrid switch.</summary>
    public HypergridSwitchModel? Switch { get; private set; }

    /// <summary>Set after a save: the "restart your world" notice (no extra notes).</summary>
    public IReadOnlyList<string>? SavedNotes { get; private set; }

    public string? Problem { get; private set; }

    public void OnGet() => Fill(settings());

    public IActionResult OnPostChange(string? to)
    {
        var s = settings();
        var target = PlanCatalog.ById(to);
        if (s.Plan is not { } from || target is null)
        {
            Problem = "That isn't a plan you can change to.";
            Fill(s);
            return Page();
        }
        var (allowed, reason) = PlanCatalog.CanChange(from, target.Plan);
        if (!allowed)
        {
            Problem = reason;
            Fill(s);
            return Page();
        }

        var saved = grid.UpdateSettings(x => x with
        {
            Plan = target.Plan,
            // Local has no hypergrid; the other plans keep the owner's choice (it only counts with a public address).
            Hypergrid = target.Plan != OwnerPlan.Local && x.Hypergrid,
            // Local can't give out a public address; a custom network hostname stays.
            Hostname = target.Plan == OwnerPlan.Local && HostAddress.IsPublic(x.Hostname) ? null : x.Hostname,
        }, writeConfig: true);

        // Only Simple Public Server needs a public address; a Grid Asset Server may stay on the network.
        if (target.Plan == OwnerPlan.SimplePublic && !HostAddress.IsPublic(saved.Hostname))
        {
            TempData["PlanNote"] = FinishNote;
            return Redirect("/advanced/network");
        }
        SavedNotes = [];
        Fill(saved);
        return Page();
    }

    public IActionResult OnPostHypergrid()
    {
        var s = settings();
        if (!Offered(s) || !PlanCatalog.HypergridAllowed(s))
        {
            Problem = NeedsPublicAddress;
            Fill(s);
            return Page();
        }
        var on = IsOn(Request.Form["Hypergrid"].ToString());
        var saved = grid.UpdateSettings(x => x with { Hypergrid = on }, writeConfig: true);
        SavedNotes = [];
        Fill(saved);
        return Page();
    }

    private const string NeedsPublicAddress = "Hypergrid needs a public address";

    private static bool Offered(LauncherSettings s) =>
        s.Plan is { } p && PlanCatalog.Get(p).Hypergrid == HypergridCapability.Switch;

    private void Fill(LauncherSettings s)
    {
        var plan = s.Plan ?? OwnerPlan.Local;
        Current = PlanCatalog.Get(plan);
        Advisories = PlanCatalog.AdvisoriesFor(Current, HostAddress.IsPublic(s.Hostname));
        Changes = PlanCatalog.All.Where(p => p.Plan != plan)
            .Select(p => { var (ok, why) = PlanCatalog.CanChange(plan, p.Plan); return (p, ok, why); })
            .ToList();
        Switch = Offered(s)
            ? new HypergridSwitchModel(PlanCatalog.HypergridOn(s), PlanCatalog.HypergridAllowed(s),
                PlanCatalog.HypergridAllowed(s) ? null : NeedsPublicAddress + ".", s.RobustPublicPort, s.RegionPort)
            : null;
    }

    private static bool IsOn(string value) => value != "" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
}
