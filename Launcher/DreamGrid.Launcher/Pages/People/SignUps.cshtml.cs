using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// Every sign-up on the board (StaffPolicy by the /People folder convention; <c>/people/sign-ups</c>; 404 where there is no guest
/// side): everyone waiting plus 14 days of finished ones, searched by name, sorted, <see cref="SignUpRows.PageSize"/> to a page
/// (spec 2026-11-01-sign-up-approval-design §7). Approve posts here from this page and from the dashboard card; the right to approve
/// is checked again by <see cref="SignUpApprovals"/>, so a post from someone the buttons weren't drawn for changes nothing.
/// </summary>
public sealed class SignUpsModel(SignUpBoard board, SignUpApprovals approvals, Func<LauncherSettings> settings, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    [TempData] public string? Notice { get; set; }

    public SignUpRowsView Rows { get; private set; } = new([], false, SignUpRows.ListPath, DateTimeOffset.MinValue);
    public int Pages { get; private set; } = 1;

    public void OnGet()
    {
        var all = SignUpRows.Query(board.Shown(), Q, Sort, Dir);
        Pages = Math.Max(1, (all.Count + SignUpRows.PageSize - 1) / SignUpRows.PageSize);
        PageNumber = Math.Clamp(PageNumber, 1, Pages);
        var shown = all.Skip((PageNumber - 1) * SignUpRows.PageSize).Take(SignUpRows.PageSize).ToList();
        Rows = new(shown, ApprovalRules.CanApprove(StaffSignIn.RoleOf(User), settings()), SignUpRows.ListPath, time.GetUtcNow());
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, string? returnUrl, CancellationToken ct)
    {
        var r = await approvals.ApproveAsync(id, User.Identity?.Name ?? "", StaffSignIn.RoleOf(User), HttpContext.Connection.RemoteIpAddress, ct);
        Notice = Message(r, "Approved.");
        return LocalRedirect(SignUpRows.SafeReturn(returnUrl, Url));
    }

    /// <summary>The sentence staff see after an approve or deny.</summary>
    public static string Message(ApprovalOutcome r, string done) => r.Result switch
    {
        ApprovalResult.Done => done,
        ApprovalResult.NotAllowed => "You can't approve or deny sign-ups on this grid.",
        ApprovalResult.NotFound => "That sign-up isn't waiting any more.",
        _ => r.Message ?? "",
    };

    /// <summary>A link to this page with the same search and sort, on <paramref name="page"/>, or sorted by <paramref name="sort"/>.</summary>
    public string Link(int? page = null, string? sort = null)
    {
        var dir = sort is null ? Dir : sort == Sort && Dir != "desc" ? "desc" : "asc";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(Q)) query.Add("q=" + Uri.EscapeDataString(Q));
        if ((sort ?? Sort) is { } s) query.Add("sort=" + s);
        if (dir is not null) query.Add("dir=" + dir);
        if (page is > 1) query.Add("p=" + page);
        return SignUpRows.ListPath + (query.Count > 0 ? "?" + string.Join("&", query) : "");
    }
}
