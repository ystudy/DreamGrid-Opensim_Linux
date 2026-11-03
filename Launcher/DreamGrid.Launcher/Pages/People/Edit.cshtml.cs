using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// Editing a resident's name and email (<c>/people/{id}/edit</c>): the owner and admins only (a supervisor gets 403 on the page and
/// the post). <see cref="PeopleActions.EditAsync"/> checks the rules again, writes, tells the person by email and audits.
/// </summary>
public sealed class EditModel(PeopleActions people, ILogger<EditModel> log) : PageModel
{
    public PersonView? Person { get; private set; }

    [BindProperty] public string? First { get; set; }
    [BindProperty] public string? Last { get; set; }
    [BindProperty] public string? Email { get; set; }

    public string? Refusal { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        if (!MayEdit()) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await LoadAsync(id, ct)) return NotFound();
        First = Person!.Row.Account.FirstName;
        Last = Person.Row.Account.LastName;
        Email = Person.Row.Account.Email;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        if (!MayEdit()) return StatusCode(StatusCodes.Status403Forbidden);
        var outcome = await people.EditAsync(User, HttpContext.Connection.RemoteIpAddress?.ToString(), id, First, Last, Email, ct);
        if (outcome.Done) Notice = outcome.Message;
        else Error = outcome.Message;
        if (!await LoadAsync(id, ct)) return outcome.Done ? Page() : NotFound();
        if (outcome.Done)
        {
            First = Person!.Row.Account.FirstName;
            Last = Person.Row.Account.LastName;
            Email = Person.Row.Account.Email;
        }
        return Page();
    }

    private bool MayEdit() => StaffSignIn.RoleOf(User) is StaffRole.Admin or StaffRole.Owner;

    private async Task<bool> LoadAsync(Guid id, CancellationToken ct)
    {
        try
        {
            Person = await people.ViewAsync(User, id, ct);
        }
        catch (PeopleDirectoryUnavailableException e)
        {
            log.LogWarning("People: a person could not be read: {Message}", e.InnerException?.Message ?? e.Message);
            Error ??= PeopleActions.DatabaseDown;
            return false;
        }
        if (Person is null) return false;
        Refusal = people.Refusal(User, Person, PersonTask.Edit);
        return true;
    }
}
