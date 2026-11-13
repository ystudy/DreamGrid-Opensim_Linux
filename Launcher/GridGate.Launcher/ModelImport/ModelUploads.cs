using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.ModelImport;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Security;

namespace GridGate.Launcher.ModelImport;

/// <summary>A checked upload as the report page shows it: the options it was checked with and the converter's report.</summary>
public sealed record UploadJob(string Id, ImportOptions Options, ImportReport Report, bool Finished);

/// <summary><see cref="Finished"/>: the job is being made or was made, so it isn't checked again.</summary>
public enum CheckStatus { Checked, Busy, Unreadable, Finished }

/// <summary><see cref="JobId"/> is set when <see cref="Status"/> is <see cref="CheckStatus.Checked"/>.</summary>
public sealed record CheckResult(CheckStatus Status, string? JobId);

/// <summary><see cref="Busy"/>: another Make or a check holds the install's one slot; nothing was done.</summary>
public enum MakeStatus { Made, AlreadyFinished, Refused, Blocked, Failed, NotFound, Busy }

/// <summary>What Make the item did. <see cref="Report"/> and <see cref="ItemName"/> are set when it was made (or made before).</summary>
public sealed record MakeResult(MakeStatus Status, string? ItemName = null, ImportReport? Report = null, string? Message = null);

/// <summary>Who is uploading, for the audit log.</summary>
public sealed record Uploader(Guid Id, string Name, string? Ip);

/// <summary>
/// The guest's model uploads (spec 2026-11-10-model-import-design, "The guest side"): check a file in a fresh job folder under the
/// install's one conversion slot, read a job back for its report, and Make the item from what the check built. The converter's
/// output is never trusted: <c>manifest.json</c> is checked before anything is written to Robust.
/// </summary>
public sealed class ModelUploads(ImportJobs jobs, LauncherPaths paths, IModelConverter converter, IRobustAssets robust, IUploadCharge charge,
    AuditLog audit, Func<LauncherSettings> settings, TimeProvider time, ILogger<ModelUploads> log)
{
    /// <summary>Robust's inventory Name column holds 64 characters; one is kept spare.</summary>
    public const int MaxNameLength = 63;
    /// <summary>Robust's asset Name column.</summary>
    private const int MaxAssetNameLength = 64;
    private const int MaxDescriptionLength = 128;
    private const long MaxAssetBytes = 64L * 1024 * 1024;

    private const string UploadFile = "upload.json";
    private const string ReportFile = "report.json";
    private const string ManifestFile = "manifest.json";
    private const string DoneFile = "done";
    private const string InputName = "input";
    private const string AssetFolder = "assets";

    // OpenSim's PermissionMask bits (OpenSim/Framework/Util.cs); the launcher can't reference OpenSim.
    private const uint TransferBit = 1 << 13;
    private const uint ModifyBit = 1 << 14;
    private const uint CopyBit = 1 << 15;
    private const uint MoveBit = 1 << 19;

    // OpenMetaverse's asset types for a texture, an object and a mesh: the only kinds an upload makes.
    private const int TextureType = 0;
    private const int ObjectType = 6;
    private const int MeshType = 49;

    /// <summary>Items made in the last hour, so Make posted again for a job that is gone shows the done page again.</summary>
    private readonly ConcurrentDictionary<string, (Guid Guest, string ItemName, ImportReport Report, DateTimeOffset At)> _made = new();

    /// <summary>
    /// A name as the guest may see and keep it: the last path part only, no control characters or <c>&lt;</c> <c>&gt;</c>, no space at
    /// either end, at most <see cref="MaxNameLength"/> characters (never half a character pair). Empty when nothing is left.
    /// </summary>
    public static string CleanName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var last = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..];
        var kept = new StringBuilder(last.Length);
        foreach (var c in last)
            if (!char.IsControl(c) && c is not ('<' or '>')) kept.Append(c);
        var s = kept.ToString().Trim();
        if (s.Length > MaxNameLength) s = s[..(char.IsHighSurrogate(s[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength)].TrimEnd();
        return s;
    }

    /// <summary>
    /// Saves <paramref name="upload"/> as <c>input&lt;extension&gt;</c> in a new job and checks it. The guest's other unfinished jobs
    /// (no <c>done</c> marker) are deleted first: one job per guest at a time.
    /// </summary>
    public Task<CheckResult> CheckNewAsync(Guid guest, Stream upload, string extension, ImportOptions options, CancellationToken ct) =>
        CheckAsync(guest, options, replaceOthers: true, async dir =>
        {
            var input = Path.Combine(dir, InputName + extension);
            await using var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write);
            await upload.CopyToAsync(file, ct);
            return input;
        }, ct);

    /// <summary>Checks the file of <paramref name="oldJob"/> again in a new job with <paramref name="options"/>; the old job is deleted once it worked.</summary>
    public async Task<CheckResult> CheckAgainAsync(Guid guest, string oldJob, ImportOptions options, CancellationToken ct)
    {
        if (!jobs.Owns(guest, oldJob)) return new CheckResult(CheckStatus.Unreadable, null);
        var oldDir = JobDir(oldJob);
        var oldDone = Path.Combine(oldDir, DoneFile);
        if (File.Exists(oldDone)) return new CheckResult(CheckStatus.Finished, null);
        var oldInput = Directory.Exists(oldDir) ? Directory.EnumerateFiles(oldDir, InputName + ".*").FirstOrDefault() : null;
        if (oldInput is null) return new CheckResult(CheckStatus.Unreadable, null);
        var result = await CheckAsync(guest, options, replaceOthers: false, dir =>
        {
            var input = Path.Combine(dir, Path.GetFileName(oldInput));
            File.Copy(oldInput, input);
            return Task.FromResult(input);
        }, ct);
        if (result.Status != CheckStatus.Checked) return result;
        // Make may have marked the old job while this check ran: then it is Make's, and the new job goes instead.
        if (File.Exists(oldDone))
        {
            jobs.Delete(result.JobId!);
            return new CheckResult(CheckStatus.Finished, null);
        }
        jobs.Delete(oldJob);
        return result;
    }

    /// <summary>The guest's own job, or null when it isn't theirs, is gone or can't be read.</summary>
    public UploadJob? Read(Guid guest, string jobId)
    {
        if (!jobs.Owns(guest, jobId)) return null;
        var dir = JobDir(jobId);
        var options = Load<ImportOptions>(Path.Combine(dir, UploadFile));
        var report = Load<ImportReport>(Path.Combine(dir, ReportFile));
        if (options is null || report is null) return null;
        return new UploadJob(jobId, options, report, File.Exists(Path.Combine(dir, DoneFile)));
    }

    /// <summary>The item already made from <paramref name="jobId"/> for this guest, if any.</summary>
    public (string ItemName, ImportReport Report)? Made(Guid guest, string jobId) =>
        _made.TryGetValue(jobId, out var m) && m.Guest == guest ? (m.ItemName, m.Report) : null;

    /// <summary>
    /// Makes the item, one at a time on the whole install (under the conversion slot, so never beside a check either): the job's
    /// manifest is checked, also that Robust holds none of its asset ids yet, the job is marked finished (so a second request
    /// does nothing), the charge is asked, then every asset, the Uploads folder and the item go to Robust, the upload is audited
    /// and the job is deleted.
    /// </summary>
    public async Task<MakeResult> MakeAsync(Uploader who, string jobId, CancellationToken ct)
    {
        var job = Read(who.Id, jobId);
        if (job is null)
            return Made(who.Id, jobId) is { } made
                ? new MakeResult(MakeStatus.AlreadyFinished, made.ItemName, made.Report)
                : new MakeResult(MakeStatus.NotFound);
        if (job.Finished) return new MakeResult(MakeStatus.AlreadyFinished);
        if (job.Report.Blocked) return new MakeResult(MakeStatus.Blocked);

        if (!jobs.TryEnterSlot()) return new MakeResult(MakeStatus.Busy);
        try { return await MakeInSlotAsync(who, jobId, job, ct); }
        finally { jobs.LeaveSlot(); }
    }

    private async Task<MakeResult> MakeInSlotAsync(Uploader who, string jobId, UploadJob job, CancellationToken ct)
    {
        var dir = JobDir(jobId);
        var manifest = CheckedManifest(dir, who.Id, job.Options, out var problem);
        if (manifest is not null)
        {
            // The ids are the converter's: one Robust already holds would be overwritten (or refused) and the item made wrong.
            try
            {
                foreach (var asset in manifest.Assets)
                    if (await robust.ExistsAsync(asset.Id, ct))
                    {
                        problem = $"asset {asset.Id:D} is already in Robust";
                        manifest = null;
                        break;
                    }
            }
            catch (RobustAssetsUnavailableException e)
            {
                problem = e.Message;
                manifest = null;
            }
        }
        if (manifest is null)
        {
            log.LogError("Model import: the upload {Job} was not made: {Problem}", jobId, problem);
            jobs.Delete(jobId);
            return new MakeResult(MakeStatus.Failed);
        }

        // Written before anything else and only once (CreateNew): a second request finds it and writes nothing.
        var done = Path.Combine(dir, DoneFile);
        try { await using (new FileStream(done, FileMode.CreateNew, FileAccess.Write)) { } }
        catch (IOException) { return new MakeResult(MakeStatus.AlreadyFinished); }

        try
        {
            // The payment project must revisit this order: today the charge is free, so "nothing was charged" is true when a write
            // below fails. A real charge must move after the writes, or be refunded on failure (spec: open question).
            var charged = await charge.TryChargeAsync(who.Id, job.Report, ct);
            if (!charged.Allowed)
            {
                File.Delete(done);
                // No message: the page says, in its language, that the upload isn't allowed right now.
                return new MakeResult(MakeStatus.Refused, Message: charged.Message);
            }

            // The object last: it points at the textures and meshes, which must be there first.
            foreach (var asset in manifest.Assets.OrderBy(a => a.Type == ObjectType))
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(dir, asset.File), ct);
                await robust.StoreAsync(asset.Id, asset.Type, Cut(asset.Name, MaxAssetNameLength), bytes, who.Id, ct);
            }
            var folder = await robust.UploadsFolderAsync(who.Id, ct);
            await robust.AddItemAsync(who.Id, folder, manifest.Item, ct);

            audit.Write(new AuditEntry(who.Name, who.Id, AuthSchemes.GuestRole, who.Ip, "model-uploaded",
                $"{manifest.Item.Name} from {job.Options.FileName}, assets {string.Join(", ", manifest.Assets.Select(a => a.Id.ToString("D")))}", "ok"));
            Remember(jobId, who.Id, manifest.Item.Name, job.Report);
            jobs.Delete(jobId);
            return new MakeResult(MakeStatus.Made, manifest.Item.Name, job.Report);
        }
        catch (Exception e)
        {
            log.LogError(e, "Model import: making the item for upload {Job} failed: {Message}", jobId, e.Message);
            jobs.Delete(jobId);
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            return new MakeResult(MakeStatus.Failed);
        }
    }

    /// <summary>
    /// Runs the check in a new job under the conversion slot, which is left whatever happens. With <paramref name="replaceOthers"/>
    /// the guest's unfinished jobs are deleted before the new one is made.
    /// </summary>
    private async Task<CheckResult> CheckAsync(Guid guest, ImportOptions options, bool replaceOthers, Func<string, Task<string>> save,
        CancellationToken ct)
    {
        if (!jobs.TryEnterSlot()) return new CheckResult(CheckStatus.Busy, null);
        string? jobId = null;
        try
        {
            if (replaceOthers)
                foreach (var other in jobs.JobsOf(guest))
                    if (!File.Exists(Path.Combine(JobDir(other), DoneFile)))
                        jobs.Delete(other);
            var dir = jobs.Create(guest);
            jobId = Path.GetFileName(dir);
            var input = await save(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, UploadFile), JsonSerializer.Serialize(options, ContractJson.Options), ct);
            var run = await converter.CheckAsync(input, ImportLimitsRules.For(settings()), options, dir, ct);
            if (run.Outcome == ConverterOutcome.Done && run.Report is not null) return new CheckResult(CheckStatus.Checked, jobId);
            jobs.Delete(jobId);
            return new CheckResult(CheckStatus.Unreadable, null);
        }
        catch (Exception e)
        {
            if (jobId is not null) jobs.Delete(jobId);
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            log.LogError(e, "Model import: checking an upload failed: {Message}", e.Message);
            return new CheckResult(CheckStatus.Unreadable, null);
        }
        finally
        {
            jobs.LeaveSlot();
        }
    }

    /// <summary>
    /// The manifest when it is sound: only textures, meshes and one object; every file inside the job's assets folder and there; at most
    /// 64 MB in all; the item points at the object, is made by this guest, and its name and description fit Robust's columns.
    /// </summary>
    private static ImportManifest? CheckedManifest(string dir, Guid guest, ImportOptions options, out string problem)
    {
        var manifest = Load<ImportManifest>(Path.Combine(dir, ManifestFile));
        if (manifest?.Assets is not { Count: > 0 } assets || manifest.Item is not { } item)
        {
            problem = "the manifest is missing or empty";
            return null;
        }
        var root = Path.GetFullPath(Path.Combine(dir, AssetFolder)) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var a in assets)
        {
            if (a.Type is not (TextureType or MeshType or ObjectType)) { problem = $"asset {a.Id} has type {a.Type}"; return null; }
            if (a.Id == Guid.Empty || string.IsNullOrEmpty(a.File)) { problem = "an asset has no id or file"; return null; }
            var full = Path.GetFullPath(Path.Combine(dir, a.File));
            if (!full.StartsWith(root, StringComparison.Ordinal)) { problem = $"asset {a.Id} is outside the assets folder"; return null; }
            if (!File.Exists(full)) { problem = $"asset {a.Id} has no file"; return null; }
            total += new FileInfo(full).Length;
        }
        var objects = assets.Where(a => a.Type == ObjectType).ToList();
        problem =
            assets.Select(a => a.Id).Distinct().Count() != assets.Count ? "two assets share an id"
            : objects.Count != 1 ? $"there are {objects.Count} objects"
            : total > MaxAssetBytes ? $"the assets are {total} bytes"
            : item.AssetId != objects[0].Id ? "the item doesn't point at the object"
            : item.CreatorId != guest ? "the item's creator isn't the uploader"
            : string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > MaxNameLength ? "the item's name is empty or too long"
            : item.Name != options.ItemName ? "the item's name isn't the one the guest was shown"
            : item.NextOwnerMask != NextOwnerMask(options.NextOwner) ? "the item's next-owner permissions aren't the ones the guest was shown"
            : item.Description is null || item.Description.Length > MaxDescriptionLength ? "the item's description is too long"
            : "";
        return problem.Length == 0 ? manifest : null;
    }

    /// <summary>The mask the converter writes for <paramref name="next"/> (ObjectAssetBuilder.NextOwnerMask): none ticked means Transfer; Move always.</summary>
    public static uint NextOwnerMask(NextOwner next)
    {
        var mask = (next.Copy ? CopyBit : 0) | (next.Modify ? ModifyBit : 0) | (next.Transfer ? TransferBit : 0);
        return (mask == 0 ? TransferBit : mask) | MoveBit;
    }

    private void Remember(string jobId, Guid guest, string itemName, ImportReport report)
    {
        var now = time.GetUtcNow();
        foreach (var (id, m) in _made)
            if (now - m.At > ImportJobs.MaxAge) _made.TryRemove(id, out _);
        _made[jobId] = (guest, itemName, report, now);
    }

    private string JobDir(string jobId) => Path.Combine(paths.Uploads, jobId);

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(char.IsHighSurrogate(s[max - 1]) ? max - 1 : max)];

    private static T? Load<T>(string file) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(file), ContractJson.Options); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
