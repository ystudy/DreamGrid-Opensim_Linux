using DreamGrid.Launcher.Core.Estates;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Estates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Estates;

/// <summary>
/// The admin's list of this computer's estates (<c>/estates</c>; AdminPolicy by the folder rule, 404 where there is no guest side),
/// and making a new one (name and owner). Changes go through <see cref="EstatesAdmin"/>.
/// </summary>
public sealed class IndexModel(IGridEstates estates, EstatesAdmin admin, IRobustClient robust, ILogger<IndexModel> log) : PageModel
{
    /// <summary>The sort keys the column headings offer; "-" in front means largest or last first.</summary>
    public static readonly string[] SortKeys = ["name", "owner", "regions", "managers"];

    /// <summary>The estates to show: matching <see cref="Q"/>, in <see cref="Sort"/> order.</summary>
    public IReadOnlyList<EstateSummary> Rows { get; private set; } = [];
    /// <summary>How many estates there are before the search narrows them.</summary>
    public int Total { get; private set; }
    /// <summary>The search text: part of an estate's name or its owner's name, any case.</summary>
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>One of <see cref="SortKeys"/>, maybe with a leading "-"; anything else sorts by name.</summary>
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    public bool Unavailable { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public string? TypedName { get; private set; }
    public string? TypedOwner { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Notice = TempData["EstatesNotice"] as string;
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? owner, CancellationToken ct)
    {
        EstateOutcome outcome;
        if (EstatesAdmin.NameProblem(name) is { } bad) outcome = new(false, bad);
        else
        {
            var (account, problem) = await AvatarLookup.FindAsync(robust, owner, log, ct);
            outcome = account is null ? new(false, problem!) : await admin.CreateAsync(User, Ip, name, account, ct);
        }
        if (outcome is { Done: true, Estate: { } made })
        {
            TempData["EstateNotice"] = outcome.Message;
            return Redirect($"/estates/estate?id={made}");
        }
        (Error, TypedName, TypedOwner) = (outcome.Message, name, owner);
        await LoadAsync(ct);
        return Page();
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task LoadAsync(CancellationToken ct)
    {
        IReadOnlyList<EstateSummary> all;
        try { all = await estates.ListAsync(ct); }
        catch (EstatesUnavailableException) { Unavailable = true; return; }
        Total = all.Count;
        Rows = Arrange(all, Q, Sort);
    }

    /// <summary>The key part of <paramref name="sort"/> ("name" when it isn't one of <see cref="SortKeys"/>).</summary>
    public static string SortKey(string? sort)
    {
        var key = sort?.TrimStart('-').ToLowerInvariant();
        return key is not null && SortKeys.Contains(key) ? key : "name";
    }

    public static bool Descending(string? sort) => sort?.StartsWith('-') == true && SortKey(sort) == sort[1..].ToLowerInvariant();

    /// <summary>Keeps the estates whose name or owner contains <paramref name="q"/> and puts them in <paramref name="sort"/> order (name breaks ties).</summary>
    public static IReadOnlyList<EstateSummary> Arrange(IEnumerable<EstateSummary> all, string? q, string? sort)
    {
        var words = q?.Trim();
        var kept = string.IsNullOrEmpty(words) ? all
            : all.Where(e => e.Name.Contains(words, StringComparison.OrdinalIgnoreCase) || e.OwnerName.Contains(words, StringComparison.OrdinalIgnoreCase));
        var byName = StringComparer.OrdinalIgnoreCase;
        var down = Descending(sort);
        IOrderedEnumerable<EstateSummary> ordered = SortKey(sort) switch
        {
            "owner" => down ? kept.OrderByDescending(e => e.OwnerName, byName) : kept.OrderBy(e => e.OwnerName, byName),
            "regions" => down ? kept.OrderByDescending(e => e.Regions) : kept.OrderBy(e => e.Regions),
            "managers" => down ? kept.OrderByDescending(e => e.Managers) : kept.OrderBy(e => e.Managers),
            _ => down ? kept.OrderByDescending(e => e.Name, byName) : kept.OrderBy(e => e.Name, byName),
        };
        return [.. SortKey(sort) == "name" ? ordered : ordered.ThenBy(e => e.Name, byName)];
    }

    /// <summary>The link a column heading goes to: that column ascending, or descending when it is already the ascending sort.</summary>
    public string SortLink(string key)
    {
        var next = SortKey(Sort) == key && !Descending(Sort) ? "-" + key : key;
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(Q)) query.Add("q=" + Uri.EscapeDataString(Q.Trim()));
        if (next != "name") query.Add("sort=" + next);
        return "/estates" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }

    /// <summary>The heading's aria-sort value: ascending, descending or none.</summary>
    public string AriaSort(string key) => SortKey(Sort) != key ? "none" : Descending(Sort) ? "descending" : "ascending";
}
