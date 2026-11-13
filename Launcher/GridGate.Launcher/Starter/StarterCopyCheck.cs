using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Starter;
using GridGate.Launcher.Core.Security;

namespace GridGate.Launcher.Starter;

/// <summary>
/// Checks, after a sign-up, that Robust copied the starter avatar's whole outfit (spec 2026-11-11-starter-avatars-design §5.5).
/// Robust stores the account before it copies the outfit, so this polls until the copy is complete or <see cref="Wait"/> has
/// passed. An incomplete copy takes the avatar off the sign-up page with a warning for the owner. A database that can't be asked
/// leaves the avatar as it is: that says nothing about the model.
/// </summary>
public sealed class StarterCopyCheck(IAppearanceStore looks, IInventoryStore inventory, StarterAvatarList starters, AuditLog audit,
    TimeProvider time, ILogger<StarterCopyCheck> log)
{
    public TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);

    public async Task RunAsync(Guid resident, StarterAvatar avatar, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + Wait;
        CheckResult result;
        try
        {
            while (true)
            {
                result = await CompareAsync(resident, avatar, ct);
                if (result.Passed) return;
                if (time.GetUtcNow() >= deadline) break;
                await Task.Delay(Poll, time, ct);
            }
        }
        catch (StaffDirectoryUnavailableException e)
        {
            log.LogWarning("The starter avatar copy for {Avatar} couldn't be checked: {Message}", avatar.Name, e.Message);
            return;
        }

        var warning = $"{avatar.Name} wasn't copied to a new resident; check its model";
        starters.Update(avatar.Id, a => a with { Warning = warning });
        log.LogWarning("Starter avatar {Avatar} wasn't copied completely: {Missing}", avatar.Name, string.Join(", ", result.Missing));
        audit.Write(new AuditEntry("GridGate", null, StaffRole.None.AuditName(), null, "starter-avatar-failed", LogText.Clean(avatar.Name, 64),
            result.Missing.FirstOrDefault() ?? "not copied"));
    }

    private async Task<CheckResult> CompareAsync(Guid resident, StarterAvatar avatar, CancellationToken ct)
    {
        var model = await looks.WornAsync(avatar.Model, ct) ?? [];
        var modelItems = await inventory.ItemsAsync(avatar.Model, [.. model.Select(w => w.ItemId)], ct);
        var mine = await looks.WornAsync(resident, ct);
        var mineItems = mine is null ? [] : await inventory.ItemsAsync(resident, [.. mine.Select(w => w.ItemId)], ct);
        if (model.Count == 0) return new(false, [StarterCheck.NoOutfit], []);
        return StarterCheck.Copy(model, [.. modelItems.Select(i => i.Row)], mine, [.. mineItems.Select(i => i.Row)]);
    }
}
