using DreamGrid.Launcher.Core.Groups;
using DreamGrid.Launcher.Groups;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Groups;

/// <summary>
/// The admin's list of the grid's groups (<c>/groups</c>; AdminPolicy by the folder rule, 404 where there is no guest side): name,
/// founder, members, open to join and the default group's mark, with a name filter. Groups are made in the viewer.
/// </summary>
public sealed class IndexModel(IGridGroups groups, GroupJoins joins) : PageModel
{
    public IReadOnlyList<GroupSummary> Rows { get; private set; } = [];
    public bool Unavailable { get; private set; }
    public bool Any { get; private set; }
    public Guid? DefaultGroup { get; private set; }
    public string? Query { get; private set; }
    public string? Notice { get; private set; }

    public async Task OnGetAsync(string? q, CancellationToken ct)
    {
        Query = q?.Trim();
        Notice = TempData["GroupsNotice"] as string;
        DefaultGroup = joins.Default();
        try
        {
            var all = await groups.ListAsync(ct);
            Any = all.Count > 0;
            Rows = string.IsNullOrEmpty(Query) ? all : all.Where(g => g.Name.Contains(Query, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (GroupsUnavailableException)
        {
            Unavailable = true;
        }
    }
}
