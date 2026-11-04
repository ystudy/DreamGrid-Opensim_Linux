using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// One takedown case (<c>/people/takedowns/TD-2026-001</c>), the owner and admins only: the notice, the people and items, the
/// events, and while it is open Put back, Find copies, Add a note and Close. Every post goes to <see cref="TakedownActions"/>.
/// </summary>
public sealed class TakedownModel(TakedownStore store, TakedownActions takedowns) : PageModel
{
    public TakedownCase? Case { get; private set; }
    public IReadOnlyList<CopyRow>? Copies { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public IActionResult OnGet(string number) => Load(number);

    public async Task<IActionResult> OnPostPutBackAsync(string number, Guid item, string? why, CancellationToken ct) =>
        TakedownAccess.MayUse(User) ? Show(number, await takedowns.PutBackAsync(User, Ip, number, item, why, ct)) : Forbid403();

    public async Task<IActionResult> OnPostCopiesAsync(string number, CancellationToken ct)
    {
        if (!TakedownAccess.MayUse(User)) return Forbid403();
        Copies = await takedowns.FindCopiesAsync(User, Ip, number, ct);
        if (Copies is null) Error = PeopleActions.DatabaseDown;
        return Load(number);
    }

    public async Task<IActionResult> OnPostTakeCopiesAsync(string number, string[] picks, CancellationToken ct)
    {
        if (!TakedownAccess.MayUse(User)) return Forbid403();
        var parsed = new List<(Guid Owner, Guid Item)>();
        foreach (var pick in picks)
            if (pick.Split(':') is [var o, var i] && Guid.TryParse(o, out var owner) && Guid.TryParse(i, out var item)) parsed.Add((owner, item));
        return Show(number, await takedowns.TakeCopiesAsync(User, Ip, number, parsed, ct));
    }

    public IActionResult OnPostNote(string number, string? text) =>
        TakedownAccess.MayUse(User) ? Show(number, takedowns.AddNote(User, Ip, number, text)) : Forbid403();

    public IActionResult OnPostClose(string number, string? outcome) =>
        TakedownAccess.MayUse(User) ? Show(number, takedowns.Close(User, Ip, number, outcome)) : Forbid403();

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private StatusCodeResult Forbid403() => StatusCode(StatusCodes.Status403Forbidden);

    private IActionResult Show(string number, TakedownOutcome outcome)
    {
        if (outcome.Done) Notice = outcome.Message;
        else Error = outcome.Message;
        return Load(number);
    }

    private IActionResult Load(string number)
    {
        if (!TakedownAccess.MayUse(User)) return Forbid403();
        Case = store.Get(number);
        return Case is null ? NotFound() : Page();
    }
}
