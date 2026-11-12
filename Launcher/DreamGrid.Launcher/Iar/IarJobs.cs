using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;

namespace DreamGrid.Launcher.Iar;

/// <summary>Tells a resident their backup is ready; false when it can't (email isn't set up, or they have no address).</summary>
public interface IIarNotifier
{
    Task<bool> NotifyReadyAsync(Guid principal, string avatarName, DateTimeOffset expires, CancellationToken ct);
}

/// <summary>
/// Where a resident's backup or upload is, for their page. <see cref="Stage"/> and <see cref="ErrorKey"/> are resident text keys,
/// so the page shows them in the resident's language.
/// </summary>
public sealed record IarJobSnapshot(Guid Principal, IarJobKind Kind, string Stage, long? Bytes, bool Running, string? ErrorKey, int? Items,
    IReadOnlyList<LeftOutItem> LeftOut, int BannedRemoved, string? Folder);

/// <summary>
/// Residents' backups and uploads (spec 2026-11-11-iar-backups-design §5 and §6). Each one is checked against
/// <see cref="IarRules"/> and counted under one lock, so a double click makes one job and one count, then runs in the background
/// (the launcher-lifetime token, never a request's). A count is given back when a job fails because of the grid (no region, the
/// region stopped, the launcher stopped, a download over the size limit) and kept when it fails because of the file. Every
/// staging file is deleted however a job ends, and only then does the job show as finished. Every step is audited with the resident
/// as the actor.
/// </summary>
public sealed class IarJobs(IGridController grid, Func<LauncherSettings> settings, IarRights rights, IarCounts counts, IarFiles files,
    TakedownStore takedowns, IInventoryStore inventory, LauncherPaths paths, BackgroundWork background, IIarNotifier notifier, AuditLog audit,
    TimeProvider time, ILogger<IarJobs> log, Func<long>? freeDisk = null)
{
    // Resident text keys for stages and errors.
    public const string Waiting = "Iar.Stage.Waiting";
    public const string Receiving = "Iar.Stage.Receiving";
    public const string Saving = "Iar.Stage.Saving";
    public const string Checking = "Iar.Stage.Checking";
    public const string Loading = "Iar.Stage.Loading";
    public const string Ready = "Iar.Stage.Ready";
    public const string Loaded = "Iar.Stage.Loaded";
    public const string NoRegion = "Iar.Error.NoRegion";
    public const string Failed = "Iar.Error.Failed";
    public const string Stopped = "Iar.Error.Stopped";
    public const string TooBig = "Iar.Error.TooBig";
    public const string NotInventory = "Iar.Error.NotInventory";
    public const string DownloadTooBig = "Iar.Error.DownloadTooBig";
    public const string TakedownsUnreadable = "Iar.Error.TakedownsUnreadable";

    private const long Mb = 1L << 20;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Job> _jobs = [];

    private sealed class Job(Guid principal, IarJobKind kind)
    {
        public Guid Principal { get; } = principal;
        public IarJobKind Kind { get; } = kind;
        public string Stage { get; set; } = Waiting;
        public long? Bytes { get; set; }
        public bool Running { get; set; } = true;
        public string? ErrorKey { get; set; }
        public int? Items { get; set; }
        public IReadOnlyList<LeftOutItem> LeftOut { get; set; } = [];
        public int BannedRemoved { get; set; }
        public string? Folder { get; set; }
    }

    /// <summary>The resident's running job, else their last one since the launcher started; null if none.</summary>
    public IarJobSnapshot? For(Guid principal)
    {
        lock (_gate)
            return _jobs.TryGetValue(principal, out var j)
                ? new IarJobSnapshot(j.Principal, j.Kind, j.Stage, j.Bytes, j.Running, j.ErrorKey, j.Items, j.LeftOut, j.BannedRemoved, j.Folder)
                : null;
    }

    /// <summary>The refusal for a resident starting <paramref name="kind"/> now, or null when they may (counts nothing).</summary>
    public IarRefusal? Check(Guid principal, IarJobKind kind)
    {
        lock (_gate) return RefusalUnderLock(principal, kind);
    }

    /// <summary>Starts a backup of the resident's inventory; null when it started, else why not (audited).</summary>
    public IarRefusal? TryStartDownload(Guid principal, string first, string last, string stamp, string? ip)
    {
        var name = $"{first} {last}";
        if (Start(principal, IarJobKind.Save, name, ip) is { } refusal) return refusal;
        background.Run("Inventory backup", ct => RunDownloadAsync(principal, first, last, stamp, ip, ct));
        return null;
    }

    /// <summary>
    /// Starts loading <paramref name="staged"/> (already in <see cref="LauncherPaths.IarsIncoming"/>) into the resident's
    /// inventory; null when it started, else why not (audited, and the file is removed).
    /// </summary>
    public IarRefusal? TryStartUpload(Guid principal, string first, string last, string staged, string? ip)
    {
        if (BeginUpload(principal, first, last, ip) is { } refusal)
        {
            TryDelete(staged);
            return refusal;
        }
        ContinueUpload(principal, first, last, staged, ip);
        return null;
    }

    /// <summary>
    /// Reserves an upload before any byte arrives (final review, Important 2): the rules are checked, the day's count is used and
    /// the resident's job slot is taken, so parallel uploads are refused instead of all being written to disk. Follow it with
    /// <see cref="ContinueUpload"/> or <see cref="AbandonUpload"/>.
    /// </summary>
    public IarRefusal? BeginUpload(Guid principal, string first, string last, string? ip)
    {
        var refusal = Start(principal, IarJobKind.Load, $"{first} {last}", ip);
        if (refusal is null) Set(principal, j => j.Stage = Receiving);
        return refusal;
    }

    /// <summary>Starts the reserved upload's checks and load in the background.</summary>
    public void ContinueUpload(Guid principal, string first, string last, string staged, string? ip) =>
        background.Run("Inventory upload", ct => RunUploadAsync(principal, first, last, staged, ip, ct));

    /// <summary>Ends a reserved upload that never arrived whole. The count comes back only when the browser went away or the disk was full.</summary>
    public void AbandonUpload(Guid principal, string name, IarUploadProblem problem, string? ip)
    {
        if (problem is IarUploadProblem.Stopped or IarUploadProblem.NoSpace or IarUploadProblem.BadRequest or IarUploadProblem.NoFile)
            counts.GiveBack(principal, IarJobKind.Load);
        Fail(principal, name, ip, problem == IarUploadProblem.TooBig ? TooBig : problem == IarUploadProblem.NotAnIar ? NotInventory : Stopped,
            "upload: " + problem);
        Set(principal, j => j.Running = false);
    }

    private IarRefusal? Start(Guid principal, IarJobKind kind, string name, string? ip)
    {
        lock (_gate)
        {
            if (RefusalUnderLock(principal, kind) is { } refusal)
            {
                Write(name, principal, ip, "iar-refused", $"{name}: {refusal}", "refused");
                return refusal;
            }
            counts.Use(principal, kind);
            _jobs[principal] = new Job(principal, kind);
        }
        Write(name, principal, ip, kind == IarJobKind.Save ? "iar-download-asked" : "iar-upload-asked", name, "ok");
        return null;
    }

    private IarRefusal? RefusalUnderLock(Guid principal, IarJobKind kind)
    {
        var person = rights.For(principal);
        var effective = IarRights.Effective(settings().Iar, person);
        var personOn = kind == IarJobKind.Save ? !person.DownloadOff : !person.UploadOff;
        return IarRules.Refusal(kind, effective, personOn, takedowns.NamedInOpenCase(principal), counts.UsedToday(principal, kind),
            _jobs.TryGetValue(principal, out var mine) && mine.Running, _jobs.Values.Count(j => j.Running),
            // An upload needs room for the largest file it may bring on top of the floor.
            FreeDisk() - (kind == IarJobKind.Load ? effective.MaxUploadMb * Mb : 0), !takedowns.Unreadable);
    }

    private async Task RunDownloadAsync(Guid principal, string first, string last, string stamp, string? ip, CancellationToken ct)
    {
        var name = $"{first} {last}";
        var staging = Path.Combine(paths.IarsSaving, Guid.NewGuid().ToString("N") + ".iar");
        var filtered = Path.Combine(paths.IarsSaving, Guid.NewGuid().ToString("N") + ".iar");
        var giveBack = false;
        try
        {
            Set(principal, j => j.Stage = Saving);
            await grid.RunIarJobAsync(new IarJobRequest(principal, first, last, IarJobKind.Save, staging), ct, new Reporter(this, principal));
            Set(principal, j => j.Stage = Checking);
            var left = IarPermissionFilter.Apply(staging, filtered);
            TryDelete(staging);
            var s = IarRights.Effective(settings().Iar, rights.For(principal));
            if (new FileInfo(filtered).Length > s.MaxDownloadMb * Mb)
            {
                // The count is kept: each try is a whole save on a region (final review, Important 4).
                Fail(principal, name, ip, DownloadTooBig, "too big after filtering");
                return;
            }
            var items = IarArchive.Inspect(filtered, long.MaxValue, int.MaxValue).Items.Count;
            var ready = files.Publish(principal, stamp, filtered, TimeSpan.FromHours(s.LinkHours), left, items);
            Set(principal, j => { j.Stage = Ready; j.Items = items; j.LeftOut = left; j.Bytes = ready.Bytes; });
            Write(name, principal, ip, "iar-download-ready", $"{name}: {items} items, {left.Count} left out", "ok");
            await notifier.NotifyReadyAsync(principal, name, ready.Expires, ct);
        }
        catch (Exception e)
        {
            giveBack = GridFault(e, ct);
            Fail(principal, name, ip, KeyFor(e, ct), e.Message);
        }
        finally
        {
            if (giveBack) counts.GiveBack(principal, IarJobKind.Save);
            TryDelete(staging);
            TryDelete(filtered);
            Set(principal, j => j.Running = false);
        }
    }

    private async Task RunUploadAsync(Guid principal, string first, string last, string staged, string? ip, CancellationToken ct)
    {
        var name = $"{first} {last}";
        var clean = Path.Combine(paths.IarsIncoming, Guid.NewGuid().ToString("N") + ".clean.iar");
        var giveBack = false;
        try
        {
            Set(principal, j => j.Stage = Checking);
            var s = IarRights.Effective(settings().Iar, rights.For(principal));
            IarContents contents;
            try
            {
                if (!IarArchive.LooksLikeIar(staged)) throw new InvalidDataException("not an archive");
                contents = IarArchive.Inspect(staged, s.MaxUploadMb * Mb * s.UnpackFactor, s.MaxUploadItems);
            }
            catch (IarTooBigException e)
            {
                Fail(principal, name, ip, TooBig, $"over the {e.Limit} limit");
                return;
            }
            catch (InvalidDataException e)
            {
                Fail(principal, name, ip, NotInventory, e.Message);
                return;
            }
            if (contents.Items.Count == 0 && contents.Folders == 0)
            {
                Fail(principal, name, ip, NotInventory, "no inventory in it");
                return;
            }

            // The upload is never loaded as it came: it is always rewritten, without banned items and assets, into a file written
            // the way OpenSim reads archives, so what OpenSim loads is exactly what was checked (final review, Critical 2 and 3).
            var banned = await takedowns.BannedAssetsAsync(inventory, ct);
            var matches = IarBannedFilter.Find(staged, banned, takedowns.Unreadable);
            var dropped = IarBannedFilter.Clean(staged, clean, matches, banned);
            var file = clean;
            if (dropped > 0)
            {
                foreach (var group in matches.GroupBy(m => m.CaseNumber))
                    takedowns.AppendUploadMatch(group.Key, principal, name, group.Count(), time.GetUtcNow());
                Write(name, principal, ip, "iar-banned-removed", $"{name}: {matches.Count} items", "ok");
            }

            var root = (await inventory.FoldersAsync(principal, ct)).FirstOrDefault(f => f.Type == 8);
            var taken = root is null ? [] : (await inventory.FoldersAsync(principal, ct)).Where(f => f.Parent == root.Id).Select(f => f.Name);
            var folder = IarFolderNames.Free(IarFolderNames.Imported(time.GetUtcNow()), taken);
            await inventory.CreateFolderAsync(principal, folder, ct);

            Set(principal, j => { j.Stage = Loading; j.Folder = folder; j.BannedRemoved = matches.Count; });
            var loaded = await grid.RunIarJobAsync(new IarJobRequest(principal, first, last, IarJobKind.Load, file, folder), ct, new Reporter(this, principal));
            Set(principal, j => { j.Stage = Loaded; j.Items = loaded; });
            Write(name, principal, ip, "iar-upload-loaded", $"{name}: {loaded} items into {folder}", "ok");
        }
        catch (TakedownsUnreadableException e)
        {
            giveBack = true;
            Fail(principal, name, ip, TakedownsUnreadable, e.Message);
        }
        catch (Exception e)
        {
            giveBack = GridFault(e, ct);
            Fail(principal, name, ip, KeyFor(e, ct), e.Message);
        }
        finally
        {
            if (giveBack) counts.GiveBack(principal, IarJobKind.Load);
            TryDelete(staged);
            TryDelete(clean);
            Set(principal, j => j.Running = false);
        }
    }

    // A failure of the grid (no region, a region that stopped, a stop of the launcher) gives the day's count back.
    private static bool GridFault(Exception e, CancellationToken ct) =>
        ct.IsCancellationRequested || e is InvalidOperationException { Message: IarJobErrors.NoRegion or IarJobErrors.Busy or IarJobErrors.Stopped };

    private static string KeyFor(Exception e, CancellationToken ct) =>
        ct.IsCancellationRequested ? Stopped
        : e is InvalidOperationException { Message: IarJobErrors.NoRegion or IarJobErrors.Busy } ? NoRegion
        : e is InvalidOperationException { Message: IarJobErrors.Stopped } ? Stopped
        : Failed;

    private void Fail(Guid principal, string name, string? ip, string key, string why)
    {
        log.LogWarning("Inventory job for {Name} failed: {Why}", name, why);
        Set(principal, j => j.ErrorKey = key);
        Write(name, principal, ip, "iar-failed", $"{name}: {key}", "failed");
    }

    private void Set(Guid principal, Action<Job> change)
    {
        lock (_gate)
            if (_jobs.TryGetValue(principal, out var j)) change(j);
    }

    private long FreeDisk()
    {
        if (freeDisk is not null) return freeDisk();
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(paths.Iars))!).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return long.MaxValue; }
    }

    private void Write(string name, Guid principal, string? ip, string action, string target, string result) =>
        audit.Write(new AuditEntry(LogText.Clean(name, 64), principal, AuthSchemesGuestRole, ip, action, LogText.Clean(target, 140), result));

    private const string AuthSchemesGuestRole = Security.AuthSchemes.GuestRole;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class Reporter(IarJobs owner, Guid principal) : IProgress<IarJobProgress>
    {
        public void Report(IarJobProgress value) => owner.Set(principal, j => j.Bytes = value.BytesWritten ?? j.Bytes);
    }
}
