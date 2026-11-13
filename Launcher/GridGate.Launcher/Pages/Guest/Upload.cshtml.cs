using GridGate.Launcher.Core.ModelImport;
using GridGate.Launcher.Guests;
using GridGate.Launcher.ModelImport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages.Guest;

/// <summary>
/// The model upload form (GuestPolicy by the /Guest folder convention; only for a guest who may upload, see
/// <see cref="UploadPagesOnlyAttribute"/>): one <c>.glb</c> or <c>.gltf</c> file and an optional item name. The file is checked at
/// once and the guest lands on the report. Requests here may be up to the upload cap, plus room for the form around the file.
/// </summary>
[UploadPagesOnly]
[RequestSizeLimit(MaxUploadBytes + FormAllowance)]
public sealed class UploadModel(ModelUploads uploads, IStringLocalizer<ResidentText> t) : PageModel
{
    public const long MaxUploadBytes = 64L * 1024 * 1024;
    /// <summary>The multipart framing, the token and the item name around the file.</summary>
    private const long FormAllowance = 64 * 1024;

    /// <summary>ResidentText keys the report page shares.</summary>
    public const string Busy = "Upload.Busy";
    public const string Unreadable = "Upload.Unreadable";

    [BindProperty(Name = "File")] public IFormFile? Upload { get; set; }
    [BindProperty] public string? ItemName { get; set; }

    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var guest = GuestSignIn.Read(User)!.Value.Principal;
        if (Upload is not { Length: > 0 } file)
        {
            Error = t["Upload.ChooseFile"];
            return Page();
        }
        var fileName = ModelUploads.CleanName(file.FileName);
        var extension = Path.GetExtension(file.FileName.Trim()).ToLowerInvariant();
        if (extension is not (".glb" or ".gltf"))
        {
            Error = t["Upload.WrongType"];
            return Page();
        }
        if (file.Length > MaxUploadBytes)
        {
            Error = t["Upload.TooLarge", MaxUploadBytes / (1024 * 1024)];
            return Page();
        }

        var itemName = ModelUploads.CleanName(ItemName) is { Length: > 0 } named ? named : DefaultItemName(file.FileName);
        var options = new ImportOptions(itemName, fileName, 100, new NextOwner(true, true, true), guest, User.Identity?.Name ?? "");
        await using var stream = file.OpenReadStream();
        var result = await uploads.CheckNewAsync(guest, stream, extension, options, ct);
        if (result.Status == CheckStatus.Checked) return Redirect("/guest/upload/" + result.JobId);
        Error = t[result.Status == CheckStatus.Busy ? Busy : Unreadable];
        return Page();
    }

    /// <summary>The file's name without its extension, cleaned; "Model" when nothing is left (an inventory name, so the same in every language).</summary>
    public static string DefaultItemName(string fileName)
    {
        var last = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        return ModelUploads.CleanName(Path.GetFileNameWithoutExtension(last)) is { Length: > 0 } name ? name : "Model";
    }
}
