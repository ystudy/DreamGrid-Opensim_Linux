using System.Security.Cryptography;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Starter;
using Microsoft.AspNetCore.Mvc;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>A model's login, shown once: its name, the new password and where its outfit is.</summary>
public sealed record ModelLogin(string Avatar, string ModelName, string Password, string Folder);

/// <summary>A check just run, shown on the page.</summary>
public sealed record ModelCheck(string Avatar, CheckResult Result);

/// <summary>Dressing a model and checking it (spec §6.3).</summary>
public partial class StarterAvatarsModel
{
    private const string PasswordLetters = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public ModelLogin? LoginShown { get; private set; }
    public ModelCheck? CheckShown { get; private set; }

    /// <summary>
    /// Gives the model a new password, shown only in this reply, so the owner can log in and put the outfit on. The avatar comes
    /// off the sign-up page until the next <see cref="OnPostCheckAsync"/>, since the outfit may change.
    /// </summary>
    public async Task<IActionResult> OnPostLoginAsync(Guid id, CancellationToken ct)
    {
        if (Find(id) is not { } a) return Problem("That starter avatar isn't on the list any more.");
        var password = RandomNumberGenerator.GetString(PasswordLetters, 16);
        try
        {
            await passwords.SetAsync(a.Model, password, ct);
        }
        catch (RobustPasswordsUnavailableException)
        {
            return Problem("The model's password couldn't be set. Check that the grid is running and try again.");
        }
        starters.Update(a.Id, x => x with { CheckPassed = false });
        Write("starter-avatar-login", a.Name, "ok");
        LoginShown = new ModelLogin(a.Name, a.ModelName, password, a.OutfitFolder ?? StarterArchive.SafeFolder(a.Name));
        Load();
        LoadImport();
        return Page();
    }

    /// <summary>
    /// Checks the model's stored outfit (§6.3). When it passes, the avatar can go on the sign-up page, an old warning is cleared and
    /// the model gets a new password nobody sees. When it fails, the password stays, since the owner may still be dressing it.
    /// </summary>
    public async Task<IActionResult> OnPostCheckAsync(Guid id, CancellationToken ct)
    {
        if (Find(id) is not { } a) return Problem("That starter avatar isn't on the list any more.");
        CheckResult result;
        try
        {
            var worn = await looks.WornAsync(a.Model, ct);
            var items = worn is null ? [] : await inventory.ItemsAsync(a.Model, [.. worn.Select(w => w.ItemId)], ct);
            result = StarterCheck.Model(worn, [.. items.Select(i => i.Row)]);
            if (result.Passed) await passwords.SetAsync(a.Model, RandomNumberGenerator.GetString(PasswordLetters, 32), ct);
        }
        catch (Exception e) when (e is StaffDirectoryUnavailableException or RobustPasswordsUnavailableException)
        {
            return Problem("The grid's database couldn't be read. Check that the grid is running and try again.");
        }
        var now = DateTimeOffset.UtcNow;
        starters.Update(a.Id, x => x with { CheckedAt = now, CheckPassed = result.Passed, Warning = result.Passed ? null : x.Warning });
        Write("starter-avatar-checked", a.Name, result.Passed ? "passed" : "failed: " + string.Join(", ", result.Missing));
        CheckShown = new ModelCheck(a.Name, result);
        Load();
        LoadImport();
        return Page();
    }
}
