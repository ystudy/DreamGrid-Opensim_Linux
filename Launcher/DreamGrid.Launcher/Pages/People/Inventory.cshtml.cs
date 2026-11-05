using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// A person's inventory (<c>/people/{id}/inventory</c>), the owner and admins only: one folder at a time with its breadcrumb, or
/// a search by item name across every folder, Trash too, <see cref="PageSize"/> at a time. Ticked items go to the takedown form.
/// Each load is audited as <c>inventory-viewed</c>.
/// </summary>
public sealed class InventoryModel(PeopleActions people, IInventoryStore inventory, AuditLog audit) : PageModel
{
    public const int PageSize = 50;

    public PersonView? Person { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<InventoryFolder> Path { get; private set; } = [];
    public IReadOnlyList<InventoryFolder> Subfolders { get; private set; } = [];
    public IReadOnlyList<InventoryItemView> Items { get; private set; } = [];
    public InventoryFolder? Folder { get; private set; }
    public string? Q { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public bool HasMore { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid? folder, string? q, int? page, CancellationToken ct)
    {
        if (!TakedownAccess.MayUse(User)) return StatusCode(StatusCodes.Status403Forbidden);
        var (person, problem) = await people.LookAsync(User, id, ct);
        if (person is null)
        {
            if (problem is null) return NotFound();
            Error = problem;
            return Page();
        }
        Person = person;
        if (people.Refusal(User, person, PersonTask.Inventory) is { } refusal)
        {
            Error = refusal;
            return Page();
        }
        var (who, actorId, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(who, actorId, role, HttpContext.Connection.RemoteIpAddress?.ToString(), "inventory-viewed",
            LogText.Clean(person.FullName, 64), "ok"));
        PageNumber = Math.Max(1, page ?? 1);
        Q = (q ?? "").Trim() is { Length: > 0 } t ? t[..Math.Min(t.Length, PeopleDirectory.MaxSearch)] : null;
        try
        {
            var folders = await inventory.FoldersAsync(id, ct);
            var byId = folders.ToDictionary(f => f.Id);
            var skip = (PageNumber - 1) * PageSize;
            IReadOnlyList<InventoryItemView> found;
            if (Q is not null)
            {
                found = await inventory.SearchAsync(id, Q, skip, PageSize + 1, ct);
            }
            else
            {
                Folder = folder is { } f && byId.TryGetValue(f, out var chosen) ? chosen : folders.FirstOrDefault(x => x.Type == 8);
                if (Folder is null)
                {
                    Error = "They have no inventory folders.";
                    return Page();
                }
                var path = new List<InventoryFolder>();
                for (var at = Folder; at is not null && path.Count < 64; at = at.Parent is { } p && byId.TryGetValue(p, out var up) ? up : null)
                    path.Insert(0, at);
                Path = path;
                Subfolders = [.. folders.Where(x => x.Parent == Folder.Id).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)];
                found = await inventory.ItemsInAsync(id, Folder.Id, skip, PageSize + 1, ct);
            }
            HasMore = found.Count > PageSize;
            Items = [.. found.Take(PageSize)];
        }
        catch (StaffDirectoryUnavailableException)
        {
            Error = PeopleActions.DatabaseDown;
        }
        return Page();
    }

    public static string FolderName(InventoryFolder f) => f.Type == 8 ? "My Inventory" : f.Name;
}
