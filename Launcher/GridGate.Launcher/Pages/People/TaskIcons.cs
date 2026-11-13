using System.Security.Claims;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Security;

namespace GridGate.Launcher.Pages.People;

/// <summary>One task icon: what it does, its sprite symbol, and why it can't be used (null when it can).</summary>
public sealed record TaskIcon(PersonTask Task, string Symbol, string Label, string? Refusal);

/// <summary>
/// The row of task icons for a person (<c>_TaskIcons.cshtml</c>), on the list and the person's page. Edit and Delete link to
/// their pages; the others post <paramref name="PostTo"/> with the person's id, the task, and <paramref name="Back"/> (the list's
/// search, filter and page) so the answer shows the same list. <paramref name="Words"/> puts the label next to each icon.
/// </summary>
public sealed record TaskIcons(PersonView Person, IReadOnlyList<TaskIcon> Icons, string PostTo, IReadOnlyDictionary<string, string> Back, bool Words)
{
    public static TaskIcons For(PeopleActions actions, ClaimsPrincipal user, PersonView person, string postTo,
        IReadOnlyDictionary<string, string>? back = null, bool words = false)
    {
        TaskIcon Icon(PersonTask task, string symbol, string label) => new(task, symbol, label, actions.Refusal(user, person, task));
        var suspension = person.Facts.Suspended
            ? Icon(PersonTask.Unsuspend, "play", "Unsuspend")
            : Icon(PersonTask.Suspend, "pause", "Suspend");
        return new TaskIcons(person,
        [
            Icon(PersonTask.Edit, "edit", "Edit name and email"),
            Icon(PersonTask.ResetLink, "key", "Send a password reset link"),
            Icon(PersonTask.SignOut, "signout", "Sign out everywhere"),
            suspension,
            Icon(PersonTask.Delete, "trash", "Delete"),
            Icon(PersonTask.Inventory, "box", "Inventory"),
        ], postTo, back ?? new Dictionary<string, string>(), words);
    }

    /// <summary>The page an icon that is a link goes to.</summary>
    public string? LinkOf(TaskIcon icon) => icon.Task switch
    {
        PersonTask.Edit => $"/people/{Person.Row.Account.PrincipalId}/edit",
        PersonTask.Delete => $"/people/{Person.Row.Account.PrincipalId}/delete",
        PersonTask.Inventory => $"/people/{Person.Row.Account.PrincipalId}/inventory",
        _ => null,
    };
}
