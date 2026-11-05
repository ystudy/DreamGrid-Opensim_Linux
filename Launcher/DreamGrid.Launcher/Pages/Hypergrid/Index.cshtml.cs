using DreamGrid.Launcher.Core.Hypergrid;
using DreamGrid.Launcher.Hypergrid;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Hypergrid;

/// <summary>
/// The staff's hypergrid links (<c>/hypergrid</c>; StaffPolicy and GuestSideOnly from the folder rules): the list, adding one by
/// address (checked, tested, then linked by Robust), checking one, and removing one. Every change goes through
/// <see cref="HypergridAdmin"/>, which checks the rules and audits it.
/// </summary>
public sealed class IndexModel(IGridHyperlinks links, HypergridAdmin admin) : PageModel
{
    public IReadOnlyList<Hyperlink> Rows { get; private set; } = [];
    public bool Unavailable { get; private set; }
    public bool CanAdd { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public string? TypedAddress { get; private set; }
    public string? TypedX { get; private set; }
    public string? TypedY { get; private set; }

    public Task OnGetAsync(CancellationToken ct) => ShowAsync(ct);

    public async Task<IActionResult> OnPostAddAsync(string? address, string? x, string? y, CancellationToken ct)
    {
        var outcome = await admin.AddAsync(User, Ip, address, x, y, ct);
        if (!outcome.Done) (TypedAddress, TypedX, TypedY) = (address, x, y);
        await ShowAsync(ct, outcome);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid link, CancellationToken ct)
    {
        await ShowAsync(ct, await admin.RemoveAsync(User, Ip, link, ct));
        return Page();
    }

    public async Task<IActionResult> OnPostCheckAsync(Guid link, CancellationToken ct)
    {
        await ShowAsync(ct, await admin.CheckAsync(link, ct));
        return Page();
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task ShowAsync(CancellationToken ct, HyperlinkOutcome? outcome = null)
    {
        if (outcome is not null)
        {
            if (outcome.Done) Notice = outcome.Message;
            else Error = outcome.Message;
        }
        CanAdd = admin.CanAdd;
        try { Rows = await links.ListAsync(ct); }
        catch (HyperlinksUnavailableException) { Unavailable = true; }
    }
}
