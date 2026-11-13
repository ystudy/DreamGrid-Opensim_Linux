using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.People;

/// <summary>
/// Editing a resident's name and email (<c>/people/{id}/edit</c>): the owner and admins only (a supervisor gets 403 on the page and
/// the post). <see cref="PeopleActions.EditAsync"/> checks the rules again, writes, tells the person by email and audits.
/// </summary>
public sealed class EditModel(PeopleActions people) : PageModel
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
        if (!await LoadAsync(id, ct)) return Error is null ? NotFound() : Page();
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
        if (!await LoadAsync(id, ct)) return outcome.Done || Error is not null ? Page() : NotFound();
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
        var (person, problem) = await people.LookAsync(User, id, ct);
        Person = person;
        Error ??= problem;
        if (Person is null) return false;
        Refusal = people.Refusal(User, Person, PersonTask.Edit);
        return true;
    }
}
