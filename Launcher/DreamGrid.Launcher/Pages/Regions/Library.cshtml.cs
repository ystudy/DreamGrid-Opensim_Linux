using System.Globalization;
using System.Security.Cryptography;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>One file of the library page: its name, size and date in words, and the custom types made from it.</summary>
public sealed record LibraryRow(string Name, string Size, string Date, IReadOnlyList<string> Types);

/// <summary>
/// The OAR library (spec 5.5): the files with download, delete and "use as a region type", and the custom region types.
/// The upload and download are <see cref="Endpoints.LibraryApi"/>. Every settings change goes through UpdateSettings.
/// </summary>
public sealed class LibraryModel(IGridController grid, OarLibrary library, LauncherPaths paths) : PageModel
{
    private const int MinTypeName = 2;
    private const int MaxTypeName = 32;

    public IReadOnlyList<LibraryRow> Files { get; private set; } = [];

    public IReadOnlyList<CustomRegionType> Types { get; private set; } = [];

    /// <summary>The folder very large files can be copied into directly.</summary>
    public string LibraryPath => paths.Oars;

    /// <summary>Set when a delete needs a yes first: the file and the question.</summary>
    public string? Asking { get; private set; }
    public string? AskingText { get; private set; }

    /// <summary>The file whose "use as a region type" form was refused, the name typed and why.</summary>
    public string? TypeFor { get; private set; }
    public string? TypeName { get; private set; }
    public string? TypeError { get; private set; }

    [TempData] public string? Notice { get; set; }

    public void OnGet() => Load();

    /// <summary>Deletes a file and the custom types made from it, after asking first.</summary>
    public IActionResult OnPostDelete(string? name, bool confirmed)
    {
        if (Resolve(name) is not { } file) return NotFound();
        var types = grid.CurrentSettings().CustomTypes.Where(t => SameFile(t.File, file)).Select(t => t.Name).ToList();
        if (!confirmed)
        {
            Asking = file;
            AskingText = $"Delete {file}? This can't be undone." + types.Count switch
            {
                0 => "",
                1 => $" It is also the region type {types[0]}, which goes too.",
                _ => $" It is also the region types {RegionAdvice.Join(types)}, which go too.",
            };
            Load();
            return Page();
        }

        try
        {
            library.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice = $"{file} couldn't be deleted. It may be in use; try again in a moment.";
            return Redirect("/regions/library");
        }
        if (types.Count > 0)
            grid.UpdateSettings(l => l with { CustomTypes = l.CustomTypes.Where(t => !SameFile(t.File, file)).ToList() }, writeConfig: false);
        Notice = types.Count == 0
            ? $"Deleted {file}."
            : $"Deleted {file} and the region {(types.Count == 1 ? "type" : "types")} {RegionAdvice.Join(types)}.";
        return Redirect("/regions/library");
    }

    /// <summary>Saves a custom region type made from a file: a name of 2 to 32 characters, unique ignoring case.</summary>
    public IActionResult OnPostUseAsType(string? name, string? typeName)
    {
        if (Resolve(name) is not { } file) return NotFound();
        var wanted = (typeName ?? "").Trim();
        var taken = RegionTypes.All(grid.CurrentSettings())
            .FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
        var error = wanted.Length is < MinTypeName or > MaxTypeName || wanted.Any(char.IsControl)
            ? $"A region type's name has {MinTypeName} to {MaxTypeName} characters."
            : taken is not null ? $"There is already a region type called {taken.Name}." : null;
        if (error is not null)
        {
            (TypeFor, TypeName, TypeError) = (file, typeName, error);
            Load();
            return Page();
        }

        grid.UpdateSettings(l => l with { CustomTypes = [.. l.CustomTypes, new CustomRegionType(NewId(l), wanted, file)] }, writeConfig: false);
        Notice = $"{wanted} is now a region type. You can choose it when you add a region.";
        return Redirect("/regions/library");
    }

    /// <summary>Takes a custom type off the list; its file stays in the library.</summary>
    public IActionResult OnPostRemoveType(string? id)
    {
        if (id is null || !RegionTypes.IsCustom(id)
            || grid.CurrentSettings().CustomTypes.FirstOrDefault(t => t.Id == id) is not { } type)
            return NotFound();
        grid.UpdateSettings(l => l with { CustomTypes = l.CustomTypes.Where(t => t.Id != id).ToList() }, writeConfig: false);
        Notice = $"Removed {type.Name} from the list. The file stays in the library.";
        return Redirect("/regions/library");
    }

    private void Load()
    {
        var types = grid.CurrentSettings().CustomTypes;
        Types = types;
        Files = library.List().Select(f => new LibraryRow(f.Name, RegionAdvice.FileSize(f.Bytes),
            f.Modified.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture),
            types.Where(t => SameFile(t.File, f.Name)).Select(t => t.Name).ToList())).ToList();
    }

    /// <summary>The library file's name for a posted name, or null for an unsafe or missing one.</summary>
    private string? Resolve(string? name) =>
        name is not null && library.Resolve(name) is { } path ? Path.GetFileName(path) : null;

    /// <summary>File names compare as the file system does: ignoring case on Windows.</summary>
    private static bool SameFile(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary><c>c-</c> and 8 random lowercase hex characters, not yet used.</summary>
    private static string NewId(LauncherSettings s)
    {
        while (true)
        {
            var id = "c-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            if (s.CustomTypes.All(t => t.Id != id)) return id;
        }
    }
}
