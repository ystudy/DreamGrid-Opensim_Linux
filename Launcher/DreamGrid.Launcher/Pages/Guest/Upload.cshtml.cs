using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.ModelImport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// The model upload form (GuestPolicy by the /Guest folder convention; only for a guest who may upload, see
/// <see cref="UploadPagesOnlyAttribute"/>): one <c>.glb</c> or <c>.gltf</c> file and an optional item name. The file is checked at
/// once and the guest lands on the report. Requests here may be up to the upload cap, plus room for the form around the file.
/// </summary>
[UploadPagesOnly]
[RequestSizeLimit(MaxUploadBytes + FormAllowance)]
public sealed class UploadModel(ModelUploads uploads) : PageModel
{
    public const long MaxUploadBytes = 64L * 1024 * 1024;
    /// <summary>The multipart framing, the token and the item name around the file.</summary>
    private const long FormAllowance = 64 * 1024;

    public const string Busy = "The uploader is busy, try again in a minute";
    public const string Unreadable = "We couldn't read this file";

    [BindProperty(Name = "File")] public IFormFile? Upload { get; set; }
    [BindProperty] public string? ItemName { get; set; }

    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var guest = GuestSignIn.Read(User)!.Value.Principal;
        if (Upload is not { Length: > 0 } file)
        {
            Error = "Choose a file to upload.";
            return Page();
        }
        var fileName = ModelUploads.CleanName(file.FileName);
        var extension = Path.GetExtension(file.FileName.Trim()).ToLowerInvariant();
        if (extension is not (".glb" or ".gltf"))
        {
            Error = "Choose a .glb or .gltf file. In Blender, use File > Export > glTF 2.0.";
            return Page();
        }
        if (file.Length > MaxUploadBytes)
        {
            Error = "The file is larger than 64 MB.";
            return Page();
        }

        var itemName = ModelUploads.CleanName(ItemName) is { Length: > 0 } named ? named : DefaultItemName(file.FileName);
        var options = new ImportOptions(itemName, fileName, 100, new NextOwner(true, true, true), guest, User.Identity?.Name ?? "");
        await using var stream = file.OpenReadStream();
        var result = await uploads.CheckNewAsync(guest, stream, extension, options, ct);
        if (result.Status == CheckStatus.Checked) return Redirect("/guest/upload/" + result.JobId);
        Error = result.Status == CheckStatus.Busy ? Busy : Unreadable;
        return Page();
    }

    /// <summary>The file's name without its extension, cleaned; "Model" when nothing is left.</summary>
    public static string DefaultItemName(string fileName)
    {
        var last = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        return ModelUploads.CleanName(Path.GetFileNameWithoutExtension(last)) is { Length: > 0 } name ? name : "Model";
    }
}
