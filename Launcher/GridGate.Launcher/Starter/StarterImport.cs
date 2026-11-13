using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Iar;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Starter;
using GridGate.Launcher.Iar;

namespace GridGate.Launcher.Starter;

/// <summary>One avatar of an import: its name, where it is, and why it failed (null while it hasn't).</summary>
public sealed record StarterImportLine(string Name, string Stage, string? Error);

/// <summary>An import's lines, and whether it is still running.</summary>
public sealed record StarterImportProgress(IReadOnlyList<StarterImportLine> Lines, bool Running);

/// <summary>
/// Loads starter avatars from an uploaded archive (spec 2026-11-11-starter-avatars-design §6.2), one after another in the
/// background. For each: a model account <c>StarterNN Avatar</c> (names picked under one lock, skipping any already on the grid),
/// an archive of only that avatar's folder, cleaned of taken-down content (each match noted on its case), loaded into a folder named
/// after the avatar, then added to the list. A failed avatar has its model deleted and the next one goes on. The upload is deleted
/// at the end. Every avatar is audited as <c>starter-avatar-added</c>.
/// </summary>
public sealed class StarterImport(IGridController grid, IRobustClient robust, IInventoryStore inventory, TakedownStore takedowns,
    StarterAvatarList starters, IAccountRemover remover, LauncherPaths paths, BackgroundWork background, AuditLog audit, TimeProvider time,
    ILogger<StarterImport> log)
{
    public const string Waiting = "Waiting";
    public const string Making = "Making its model account";
    public const string Loading = "Loading its outfit";
    public const string Added = "Added";
    public const string Failed = "Failed";

    private readonly SemaphoreSlim _names = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (List<StarterImportLine> Lines, bool Running)> _jobs = [];

    /// <summary>Starts loading <paramref name="picks"/> (folder in the archive, display name) from <paramref name="pendingFile"/>; returns the job id.</summary>
    public string Start(IarStaffActor actor, string pendingFile, IReadOnlyList<(string Folder, string Name)> picks)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_gate) _jobs[id] = ([.. picks.Select(p => new StarterImportLine(p.Name, Waiting, null))], true);
        background.Run("Starter avatar import", async ct =>
        {
            try
            {
                for (var i = 0; i < picks.Count; i++) await OneAsync(actor, id, i, pendingFile, picks[i].Folder, picks[i].Name.Trim(), ct);
            }
            finally
            {
                TryDelete(pendingFile);
                lock (_gate) _jobs[id] = (_jobs[id].Lines, false);
            }
        });
        return id;
    }

    public StarterImportProgress? For(string id)
    {
        lock (_gate) return _jobs.TryGetValue(id, out var j) ? new([.. j.Lines], j.Running) : null;
    }

    /// <summary>
    /// Adds an account the owner already dressed by hand. Null when it isn't on the grid, is already on the list, or is anyone's own
    /// avatar (final review I2: a model's password is reset and its sign-in blocked): only an account at level 0 with no email
    /// and a name the console takes. Throws <see cref="HttpRequestException"/> when Robust can't be reached.
    /// </summary>
    public async Task<StarterAvatar?> AddExistingAsync(IarStaffActor actor, string first, string last, string name, CancellationToken ct)
    {
        try { GridConsoleCommands.ValidateAvatarName(first.Trim(), last.Trim()); }
        catch (ArgumentException) { return null; }
        if (await robust.FindAccountAsync(first.Trim(), last.Trim(), ct) is not { } account) return null;
        if (starters.IsModel(account.PrincipalId) || account.UserLevel != 0 || !string.IsNullOrWhiteSpace(account.Email)) return null;
        var a = starters.Add(name, account.PrincipalId, account.FirstName, account.LastName);
        Write(actor, a.Name, "ok");
        return a;
    }

    private async Task OneAsync(IarStaffActor actor, string job, int index, string pending, string folderPath, string name, CancellationToken ct)
    {
        var one = Path.Combine(paths.IarsSaving, Guid.NewGuid().ToString("N") + ".iar");
        var clean = Path.Combine(paths.IarsSaving, Guid.NewGuid().ToString("N") + ".iar");
        RobustAccount? model = null;
        try
        {
            Set(job, index, Making, null);
            model = await MakeModelAsync(ct);

            StarterArchive.OneAvatar(pending, folderPath, one);
            var banned = await takedowns.BannedAssetsAsync(inventory, ct);
            var matches = IarBannedFilter.Find(one, banned, takedowns.Unreadable);
            IarBannedFilter.Clean(one, clean, matches, banned);
            foreach (var group in matches.GroupBy(m => m.CaseNumber))
                takedowns.AppendUploadMatch(group.Key, model.PrincipalId, $"{model.FirstName} {model.LastName}", group.Count(), time.GetUtcNow());

            Set(job, index, Loading, null);
            var folder = StarterArchive.SafeFolder(name);
            await inventory.CreateFolderAsync(model.PrincipalId, folder, ct);
            await grid.RunIarJobAsync(new IarJobRequest(model.PrincipalId, model.FirstName, model.LastName, IarJobKind.Load, clean, folder), ct);

            starters.Add(name, model.PrincipalId, model.FirstName, model.LastName, folder + "/" + folderPath);
            Set(job, index, Added, null);
            Write(actor, name, matches.Count == 0 ? "ok" : $"{matches.Count} taken-down item{(matches.Count == 1 ? "" : "s")} left out");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Starter avatar {Name} couldn't be loaded: {Message}", name, e.Message);
            Set(job, index, Failed, e is TakedownsUnreadableException ? "The takedown records can't be read, so nothing can be checked." : e.Message);
            Write(actor, name, "failed");
            if (model is not null)
            {
                try { await remover.DeleteAsync(model.PrincipalId, CancellationToken.None); }
                catch (StaffDirectoryUnavailableException d) { log.LogWarning("The model {First} {Last} couldn't be deleted: {Message}", model.FirstName, model.LastName, d.Message); }
            }
        }
        finally
        {
            TryDelete(one);
            TryDelete(clean);
        }
    }

    /// <summary>A new model account under the lowest free <c>StarterNN Avatar</c>, never one the list or the grid already has.</summary>
    private async Task<RobustAccount> MakeModelAsync(CancellationToken ct)
    {
        await _names.WaitAsync(ct);
        try
        {
            var taken = starters.All().Select(a => a.ModelFirst).ToList();
            while (true)
            {
                var (first, last) = StarterNames.Next(taken);
                if (await robust.FindAccountAsync(first, last, ct) is not null)
                {
                    taken.Add(first);
                    continue;
                }
                try
                {
                    return await grid.CreateResidentAsync(first, last, null, 0, ct);
                }
                catch (NameTakenException)
                {
                    taken.Add(first);
                }
            }
        }
        finally { _names.Release(); }
    }

    private void Set(string job, int index, string stage, string? error)
    {
        lock (_gate)
        {
            var lines = _jobs[job].Lines;
            lines[index] = lines[index] with { Stage = stage, Error = error };
        }
    }

    private void Write(IarStaffActor actor, string name, string result) =>
        audit.Write(new AuditEntry(actor.Name, actor.Id, actor.Role, actor.Ip, "starter-avatar-added", LogText.Clean(name, 64), result));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
