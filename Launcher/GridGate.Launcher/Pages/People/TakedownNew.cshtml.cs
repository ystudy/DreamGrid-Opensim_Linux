using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.People;

/// <summary>
/// The notice form for the items ticked on a person's inventory page (<c>/people/{id}/takedown?items=…</c>), the owner and admins
/// only. It starts a new case, or adds the items to an open one. The post goes to <see cref="TakedownActions.TakeDownAsync"/>,
/// which checks everything again, and then to the case page.
/// </summary>
public sealed class TakedownNewModel(PeopleActions people, IInventoryStore inventory, TakedownStore store, TakedownActions takedowns) : PageModel
{
    public PersonView? Person { get; private set; }
    public IReadOnlyList<InventoryItemView> Chosen { get; private set; } = [];
    public IReadOnlyList<string> OpenCases { get; private set; } = [];
    public string? Error { get; private set; }

    [BindProperty] public TakedownReason Reason { get; set; } = TakedownReason.Copyright;
    [BindProperty] public string? SenderName { get; set; }
    [BindProperty] public string? Organisation { get; set; }
    [BindProperty] public string? Address { get; set; }
    [BindProperty] public string? Reference { get; set; }
    [BindProperty] public DateOnly? Received { get; set; }
    [BindProperty] public string? Text { get; set; }
    /// <summary>A checkbox: absent from the post when unticked, so it starts false and the GET ticks it for the default reason.</summary>
    [BindProperty] public bool Tell { get; set; }
    [BindProperty] public string? IntoCase { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid[] items, CancellationToken ct)
    {
        if (!TakedownAccess.MayUse(User)) return StatusCode(StatusCodes.Status403Forbidden);
        Tell = TakedownRules.DefaultTell(Reason);
        return await LoadAsync(id, items, ct) ?? Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, Guid[] items, CancellationToken ct)
    {
        if (!TakedownAccess.MayUse(User)) return StatusCode(StatusCodes.Status403Forbidden);
        var into = string.IsNullOrWhiteSpace(IntoCase) ? null : IntoCase.Trim();
        var notice = into is null
            ? new TakedownNotice(Reason, SenderName ?? "", Organisation, Address, Reference, Received ?? DateOnly.FromDateTime(DateTime.UtcNow), Text ?? "")
            : null;
        var outcome = await takedowns.TakeDownAsync(User, HttpContext.Connection.RemoteIpAddress?.ToString(), id, items, into, notice, Tell, ct);
        if (outcome.Done && outcome.Number is { } number) return Redirect("/people/takedowns/" + number);
        Error = outcome.Message;
        return await LoadAsync(id, items, ct) ?? Page();
    }

    private async Task<IActionResult?> LoadAsync(Guid id, Guid[] items, CancellationToken ct)
    {
        var (person, problem) = await people.LookAsync(User, id, ct);
        if (person is null && problem is null) return NotFound();
        Person = person;
        Error ??= problem;
        OpenCases = [.. store.All().Where(c => c.Open).Select(c => c.Number)];
        if (person is null) return null;
        try
        {
            Chosen = await inventory.ItemsAsync(id, items, ct);
        }
        catch (StaffDirectoryUnavailableException)
        {
            Error ??= PeopleActions.DatabaseDown;
        }
        if (Chosen.Count == 0) Error ??= TakedownActions.NothingPicked;
        return null;
    }
}
