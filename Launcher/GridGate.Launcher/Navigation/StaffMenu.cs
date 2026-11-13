using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Navigation;

/// <summary>A hub page's row of links to the rest of its menu group (<c>_HubLinks</c>), less the ones the page links itself.</summary>
public sealed record HubLinks(string Group, params string[] Skip);

/// <summary>One link in the staff menu. <paramref name="ActivePrefix"/> marks its group active on any page under it.</summary>
public sealed record MenuItem(string Text, string Href, string ActivePrefix, bool Badge = false);

/// <summary>
/// A top-level menu entry: a dropdown of <paramref name="Items"/> whose heading links to <paramref name="Href"/> (its hub page), so
/// the menu works without scripts. <paramref name="Right"/>: the signed-in person's own menu, at the right.
/// </summary>
public sealed record MenuGroup(string Text, string Href, IReadOnlyList<MenuItem> Items, bool Right = false)
{
    /// <summary>Shown as one plain link rather than a dropdown.</summary>
    public bool IsSingle => Items.Count <= 1;

    /// <summary>The single link's text: the group's name when its one item is the hub itself, else the item's own name.</summary>
    public string SingleText => Items.Count == 1 && Items[0].Href != Href ? Items[0].Text : Text;

    public string SingleHref => Items.Count == 1 ? Items[0].Href : Href;
}

/// <summary>
/// What the staff menu shows each person (design 2026-11-05-gridgate-menu-looks-workflows §1). It only hides links: every page keeps
/// its own access rule in Program.cs, so this must never be read as a guard.
/// </summary>
public static class StaffMenu
{
    /// <summary>The text of the right-hand group; the layout shows the person's name in its place.</summary>
    public const string You = "You";

    public static IReadOnlyList<MenuGroup> For(StaffRole role, LauncherSettings s, bool canApprove)
    {
        var staffPlans = AdminRule.HasStaffPages(s);
        var owner = role == StaffRole.Owner;
        var admin = role is StaffRole.Owner or StaffRole.Admin;
        var groups = new List<MenuGroup>();

        if (admin) groups.Add(new("Home", "/", [new("Home", "/", "/", Badge: staffPlans && canApprove)]));

        Add(groups, new("World", "/regions", Items(
            (admin, new("Regions", "/regions", "/regions")),
            (admin && staffPlans, new("Estates", "/estates", "/estates")),
            (staffPlans, new("Hypergrid links", "/hypergrid", "/hypergrid")),
            (admin, new("OAR library", "/regions/library", "/regions/library")),
            (admin, new("Recently deleted", "/regions/deleted", "/regions/deleted")))));

        if (staffPlans)
        {
            Add(groups, new("People", "/people", Items(
                (true, new("Find a person", "/people", "/people")),
                (canApprove, new("Sign-ups", "/people/sign-ups", "/people/sign-ups", Badge: !admin)),
                (true, new("Tickets", "/people/tickets", "/people/tickets")),
                (admin, new("Groups", "/groups", "/groups")),
                (admin, new("Takedowns", "/people/takedowns", "/people/takedowns")),
                (owner, new("Staff", "/staff", "/staff")))));

            Add(groups, new("Guest pages", "/guest-pages", Items(
                (admin, new("Splash page", "/splash", "/splash")),
                (owner, new("Legal pages", "/security/legal", "/security/legal")),
                (owner, new("Page wording", "/security/wording", "/security/wording")),
                (owner, new("Languages", "/security/languages", "/security/languages")),
                (owner, new("Sign-up and bots", "/security/bots", "/security/bots")),
                (owner, new("Inventory backups", "/security/iar", "/security/iar")),
                (owner, new("Starter avatars", "/security/starter-avatars", "/security/starter-avatars")),
                (owner, new("Email server", "/security/email", "/security/email")),
                (owner, new("Password reset", "/advanced/password-reset", "/advanced/password-reset")))));
        }

        if (owner) groups.Add(new("Security", "/security", [new("Security", "/security", "/security")]));

        Add(groups, new("Settings", "/advanced", Items(
            (admin, new("Plan", "/advanced/plan", "/advanced/plan")),
            (admin, new("Network", "/advanced/network", "/advanced/network")),
            (admin, new("Database", "/advanced/database", "/advanced/database")),
            (admin, new("Region defaults", "/advanced/regions", "/advanced/regions")),
            (admin, new("Logs", "/logs", "/logs")))));

        groups.Add(new(You, "/profile/sessions", Items(
            (true, new("Sessions", "/profile/sessions", "/profile/sessions")),
            (true, new("Two-factor", "/profile/two-factor", "/profile/two-factor")),
            (staffPlans, new("Email notices", "/profile/emails", "/profile/emails"))), Right: true));

        return groups;
    }

    /// <summary>
    /// The group the page at <paramref name="path"/> belongs to: the one whose hub or item address is the longest match, so
    /// /security/legal is in Guest pages rather than Security. Home matches "/" only.
    /// </summary>
    public static MenuGroup? Active(IReadOnlyList<MenuGroup> groups, string path)
    {
        MenuGroup? best = null;
        var length = -1;
        foreach (var g in groups)
            // A group shown as one link is matched by that link only, not by a hub page it doesn't link to.
            foreach (var prefix in g.IsSingle ? g.Items.Select(i => i.ActivePrefix) : g.Items.Select(i => i.ActivePrefix).Append(g.Href))
                if (Under(path, prefix) && prefix.Length > length)
                {
                    best = g;
                    length = prefix.Length;
                }
        return best;
    }

    private static bool Under(string path, string prefix) =>
        prefix == "/" ? path == "/"
            : path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    private static MenuItem[] Items(params (bool Shown, MenuItem Item)[] items) => items.Where(i => i.Shown).Select(i => i.Item).ToArray();

    private static void Add(List<MenuGroup> groups, MenuGroup g)
    {
        if (g.Items.Count > 0) groups.Add(g);
    }
}
