using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Plans;

/// <summary>Whether, and how, a plan can take part in hypergrid.</summary>
public enum HypergridCapability
{
    /// <summary>Not possible: other grids can't reach this grid.</summary>
    None,
    /// <summary>The owner has one on/off switch (needs a public address).</summary>
    Switch,
    /// <summary>Follows the Grid Asset Server this computer joins.</summary>
    FollowsGridAsset,
    /// <summary>The grid this computer joins decides.</summary>
    OtherGrid,
}

/// <summary>One owner plan as shown on its card and plan page.</summary>
/// <param name="Plan">The plan itself.</param>
/// <param name="Id">The short id used in addresses and form fields.</param>
/// <param name="Icon">The card's emoji.</param>
/// <param name="Title">The plan's name.</param>
/// <param name="Subtitle">The few words under the name.</param>
/// <param name="Summary">One or two sentences for the card.</param>
/// <param name="Details">A longer explanation for the plan page.</param>
/// <param name="Runs">What this computer runs under the plan.</param>
/// <param name="YouNeed">What the owner needs in place.</param>
/// <param name="Hypergrid">Whether the plan can use hypergrid.</param>
/// <param name="HypergridLine">One plain sentence about hypergrid under this plan.</param>
/// <param name="Advisories">Warnings and advice, written for the owner.</param>
/// <param name="Available">False while the plan is not yet buildable in this release.</param>
public sealed record PlanInfo(
    OwnerPlan Plan, string Id, string Icon, string Title, string Subtitle,
    string Summary, string Details,
    IReadOnlyList<string> Runs, IReadOnlyList<string> YouNeed,
    HypergridCapability Hypergrid, string HypergridLine,
    IReadOnlyList<string> Advisories, bool Available);

/// <summary>The five owner plans, their owner-facing text, and the rules for changing between them.</summary>
public static class PlanCatalog
{
    private const string PublicAdvice =
        "Your router must forward the ports viewers use: the grid's public port and the region ports.";
    private const string HttpAdvice =
        "The website uses plain HTTP, so passwords cross the internet unencrypted until HTTPS exists.";
    private const string CgnatAdvice =
        "Some internet providers share one address between customers; if yours does, nobody outside can reach you.";
    private const string PrivatePortAdvice =
        "Every computer on your network can reach the Grid Asset Server's private port; anyone on your network can add regions to the grid.";

    // The two Grid Asset Server advisories that depend on the address; AdvisoriesFor picks by these prefixes.
    private const string LanOnlyPrefix = "With a network-only address";
    private const string PublicPrefix = "With a public address: ";

    /// <summary>All plans in card order.</summary>
    public static IReadOnlyList<PlanInfo> All { get; } =
    [
        new(OwnerPlan.Local, "local", "🏠", "Local", "Just me & friends (LAN)",
            "A private world on your home or office network.",
            "Everything runs on this computer, and only computers on the same network can visit. It is the simplest way to start, and you can move to a public address later.",
            ["Database", "Accounts & inventory (Robust)", "Regions", "Website"],
            ["This computer", "A home or office network the visitors are on"],
            HypergridCapability.None,
            "Hypergrid is not possible: other grids can't reach a grid that is only on your network.",
            ["Only computers on this network can visit. Other grids can't reach you, so there is no hypergrid."],
            true),
        new(OwnerPlan.SimplePublic, "simple-public", "🌐", "Simple Public Server", "(WAN)",
            "A whole grid on one computer, open to visitors from the internet.",
            "Everything runs on this computer, and the grid gives out your public address so visitors can arrive from anywhere.",
            ["Database", "Accounts & inventory (Robust)", "Regions", "Website"],
            ["This computer", "A public address or name for it", "Access to your router to forward ports"],
            HypergridCapability.Switch,
            "Hypergrid is possible: one switch lets avatars travel between your grid and other grids, in both directions.",
            [PublicAdvice, HttpAdvice, CgnatAdvice],
            true),
        new(OwnerPlan.GridAsset, "grid-asset", "🗄️", "Grid Asset Server", "(LAN or WAN)",
            "The heart of a grid spread over several computers.",
            "This computer keeps the grid's accounts, inventory and assets, and other computers add regions to it. You can keep it on your network only, or give it a public address.",
            ["Database", "Accounts & inventory (Robust)", "Website", "Regions (optional, not advised)"],
            ["This computer, kept on whenever the grid is in use", "A network address the other computers can reach", "A public address too, if visitors come from the internet"],
            HypergridCapability.Switch,
            "Hypergrid is possible with a public address, with one switch for the whole grid. It is not possible when the grid is only on your network.",
            [
                "If this computer is off, every Region Server's regions stop working.",
                "You can run regions here too, but it isn't advised: busy regions slow down logins, inventory and the map for everyone on the grid.",
                PrivatePortAdvice,
                "With a network-only address, only computers on this network can visit and there is no hypergrid. You can switch to a public address later.",
                "With a public address: " + PublicAdvice + " " + HttpAdvice + " " + CgnatAdvice,
            ],
            true),
        new(OwnerPlan.RegionServer, "region-server", "🏝️", "Region Server", "Needs a Grid Asset Server",
            "Adds land to a grid whose Grid Asset Server is on another computer on your network.",
            "This computer runs regions only. Accounts, inventory and assets come from the Grid Asset Server, and hypergrid follows that grid.",
            ["Database (region data only)", "Regions", "Website"],
            ["A Grid Asset Server on another computer on your network, set up first and running", "The same DreamGrid version on both computers"],
            HypergridCapability.FollowsGridAsset,
            "Hypergrid follows the Grid Asset Server you join.",
            [
                "The Grid Asset Server must be set up first and running.",
                "Both computers should run the same DreamGrid version.",
                "Your router must forward this computer's region ports.",
                PrivatePortAdvice,
            ],
            true),
        new(OwnerPlan.Connected, "connected-server", "🔗", "Connected Server", "Uses someone else's grid",
            "Runs regions on another grid such as OSgrid. That grid keeps the accounts.",
            "This computer runs regions only, joined to a grid run by someone else.",
            ["Database (region data only)", "Regions", "Website"],
            ["An avatar on the other grid", "The grid's login address, and its address for region servers (OSgrid's are built in)", "A public address for your regions"],
            HypergridCapability.OtherGrid,
            "The other grid decides how hypergrid works.",
            [
                "The other grid's rules apply, including their sign-up and region-placement rules.",
                "Your regions must be reachable from the internet.",
                "Signing in to this computer's website uses your avatar on the other grid: that grid checks the password.",
            ],
            true),
    ];

    /// <summary>
    /// The advisories that apply once the address kind is known: the Grid Asset Server's list mixes network-only and
    /// public advice, so the other kind's entry is left out and the "With a public address:" lead-in is dropped.
    /// Other plans have one kind of address and show everything.
    /// </summary>
    public static IReadOnlyList<string> AdvisoriesFor(PlanInfo plan, bool publicAddress) =>
        plan.Plan != OwnerPlan.GridAsset
            ? plan.Advisories
            : plan.Advisories
                .Where(a => publicAddress ? !a.StartsWith(LanOnlyPrefix, StringComparison.Ordinal) : !a.StartsWith(PublicPrefix, StringComparison.Ordinal))
                .Select(a => a.StartsWith(PublicPrefix, StringComparison.Ordinal) ? a[PublicPrefix.Length..] : a)
                .ToList();

    /// <summary>The plan with this id, or null.</summary>
    public static PlanInfo? ById(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The info for a plan.</summary>
    public static PlanInfo Get(OwnerPlan plan) => All.First(p => p.Plan == plan);

    // The change matrix (spec section 8). Only moves among Local, Simple Public and Grid Asset are
    // allowed. Moving away from a Grid Asset Server also requires that no region servers are joined; that live
    // condition is checked by the Advanced Plan page, so this stays a pure matrix.
    /// <summary>Whether an install running <paramref name="from"/> may switch to <paramref name="to"/>, with a reason when it may not.</summary>
    public static (bool Allowed, string Reason) CanChange(OwnerPlan from, OwnerPlan to)
    {
        if (from == to) return (false, "This is already the plan you are running.");
        OwnerPlan[] single = [OwnerPlan.Local, OwnerPlan.SimplePublic, OwnerPlan.GridAsset];
        if (single.Contains(from) && single.Contains(to)) return (true, "");
        return (false, "Moving away from a plan that holds accounts and inventory, or to one that never had them, means starting a new install.");
    }

    /// <summary>The message when an address can't be reached from the internet but the plan needs that.</summary>
    public const string NotPublic =
        "That's an address only computers on your network can reach. Enter your public IP address or your grid's name.";

    /// <summary>The message when a Local grid is given a public address.</summary>
    public const string LocalIsNetworkOnly =
        "A Local grid uses an address on your network. To give out a public address, change your plan to Simple Public Server or Grid Asset Server first.";

    /// <summary>
    /// Why <paramref name="hostname"/> can't be this plan's address, or null when it can. Simple Public Server needs a public
    /// address (an empty one means the detected network address, so it is refused too); Local refuses a public one; every
    /// plan refuses what <see cref="SettingsValidator.HostnameError"/> refuses.
    /// </summary>
    public static string? AddressError(OwnerPlan? plan, string? hostname)
    {
        if (SettingsValidator.HostnameError(hostname) is { } error) return error;
        return plan switch
        {
            OwnerPlan.SimplePublic when !HostAddress.IsPublic(hostname) => NotPublic,
            OwnerPlan.Local when HostAddress.IsPublic(hostname) => LocalIsNetworkOnly,
            _ => null,
        };
    }

    /// <summary>Whether the hypergrid switch applies: a switch-capable plan with a public address.</summary>
    public static bool HypergridAllowed(LauncherSettings s) =>
        s.Plan is OwnerPlan.SimplePublic or OwnerPlan.GridAsset && HostAddress.IsPublic(s.Hostname);

    /// <summary>
    /// The one hypergrid sentence of a plan that uses another computer's grid (there is no switch for it), saying who
    /// decides; null on a plan that has a switch or none.
    /// </summary>
    public static string? OtherGridHypergridLine(LauncherSettings s)
    {
        var state = HypergridOn(s) ? "on" : "off";
        return s.Plan switch
        {
            OwnerPlan.Connected => $"Hypergrid is {state} for {s.Connected?.GridName ?? s.GridName}; that grid decides.",
            OwnerPlan.RegionServer => $"Hypergrid is {state} for this grid; the Grid Asset Server decides.",
            _ => null,
        };
    }

    /// <summary>Whether this computer runs Robust: not a Region Server or a Connected Server, which use another computer's.</summary>
    public static bool RunsRobust(LauncherSettings s) => s.Plan is not (OwnerPlan.RegionServer or OwnerPlan.Connected);

    /// <summary>
    /// Whether hypergrid is actually on. A Region Server follows the grid it joined; a Connected Server has it when the
    /// other grid published a gatekeeper.
    /// </summary>
    public static bool HypergridOn(LauncherSettings s) => s.Plan switch
    {
        OwnerPlan.RegionServer => s.Join?.Hypergrid ?? false,
        OwnerPlan.Connected => s.Connected?.GatekeeperUri is not null,
        _ => s.Hypergrid && HypergridAllowed(s),
    };
}
