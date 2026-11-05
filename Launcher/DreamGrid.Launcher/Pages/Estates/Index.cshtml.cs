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
    public IReadOnlyList<EstateSummary> Rows { get; private set; } = [];
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
        try { Rows = await estates.ListAsync(ct); }
        catch (EstatesUnavailableException) { Unavailable = true; }
    }
}
