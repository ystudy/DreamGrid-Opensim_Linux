using System.Text.Json;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Iar;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Residents' backups and uploads running in the background (IAR plan task 8).</summary>
public sealed class IarJobsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("dg-iarjobs-").FullName;
    private readonly LauncherPaths _paths;
    private readonly FakeGridController _grid = new();
    private readonly FakeInventoryStore _inventory = new(new FakeRobustAccounts());
    private readonly TakedownStore _takedowns;
    private readonly BackgroundWork _background = new(NullLogger<BackgroundWork>.Instance);
    private readonly FakeNotifier _notify = new();
    private readonly AuditLog _audit;
    private IarSettings _iar = new(DownloadsOn: true, UploadsOn: true);
    private long _free = 100L << 30;
    private static readonly Guid Ann = Guid.NewGuid();

    public IarJobsTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        IarFolders.Prepare(_paths);
        Directory.CreateDirectory(_paths.Guests);
        Directory.CreateDirectory(_paths.Security);
        Directory.CreateDirectory(_paths.Logs);
        _takedowns = new TakedownStore(_paths.Takedowns, TimeProvider.System, NullLogger<TakedownStore>.Instance);
        _audit = new AuditLog(Path.Combine(_paths.Logs, "audit.log"), TimeProvider.System);
        _inventory.AddDefaultFolders(Ann);
    }

    public void Dispose()
    {
        _background.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _audit.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    internal sealed class FakeNotifier : IIarNotifier
    {
        public List<(Guid Principal, string Name)> Sent { get; } = [];
        public bool Ready { get; set; } = true;
        public Task<bool> NotifyReadyAsync(Guid principal, string avatarName, DateTimeOffset expires, CancellationToken ct)
        {
            if (!Ready) return Task.FromResult(false);
            lock (Sent) Sent.Add((principal, avatarName));
            return Task.FromResult(true);
        }
    }

    private IarJobs Jobs(IarRights? rights = null) => new(_grid, () => TestSettings.Create() with { Iar = _iar },
        rights ?? new IarRights(Path.Combine(_paths.Guests, "iar-rights.json")), new IarCounts(Path.Combine(_paths.Security, "iar-counts.json"), TimeProvider.System),
        new IarFiles(_paths, TimeProvider.System), _takedowns, _inventory, _paths, _background, _notify, _audit, TimeProvider.System,
        NullLogger<IarJobs>.Instance, () => _free);

    private static async Task<IarJobSnapshot> Done(IarJobs jobs, Guid who)
    {
        var guard = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            if (jobs.For(who) is { Running: false } s) return s;
            if (DateTime.UtcNow > guard) throw new TimeoutException("the job never finished");
            await Task.Delay(20, Ct);
        }
    }

    private List<(string Action, string Target)> Audit() =>
        [.. _audit.Recent(e => e.Entry.Action.StartsWith("iar-", StringComparison.Ordinal), 100).Select(e => (e.Entry.Action, e.Entry.Target ?? ""))];

    private string Upload(Action<IarBuilder> fill)
    {
        var b = new IarBuilder();
        fill(b);
        return b.Save(Path.Combine(_paths.IarsIncoming, Guid.NewGuid().ToString("N") + ".iar"));
    }

    private int Used(IarJobKind kind) => new IarCounts(Path.Combine(_paths.Security, "iar-counts.json"), TimeProvider.System).UsedToday(Ann, kind);

    [Fact]
    public async Task Two_starts_at_once_make_one_job_and_one_count()
    {
        var gate = new TaskCompletionSource();
        _grid.OnIarJob = async (r, _, _) => { await gate.Task; FakeGridController.IarBuilderWrite(r.File); return 1; };
        var jobs = Jobs();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => jobs.TryStartDownload(Ann, "Ann", "Lee", "stamp", null))));
        Assert.Single(results, r => r is null);
        Assert.All(results.Where(r => r is not null), r => Assert.Equal(IarRefusal.Busy, r));
        gate.SetResult();
        await Done(jobs, Ann);
        Assert.Single(_grid.IarJobs);
        Assert.Equal(1, Used(IarJobKind.Save));
    }

    [Fact]
    public async Task Download_filters_then_publishes_and_removes_unfiltered_file()
    {
        _grid.OnIarJob = (r, _, _) =>
        {
            var b = new IarBuilder();
            b.Notecard("Notes", "mine", "x");
            b.Texture("Shop", "logo", IarBuilder.Copy | IarBuilder.Modify);
            b.Save(r.File);
            return Task.FromResult(2);
        };
        var jobs = Jobs();
        Assert.Null(jobs.TryStartDownload(Ann, "Ann", "Lee", "stamp", "1.2.3.4"));
        var s = await Done(jobs, Ann);

        Assert.Null(s.ErrorKey);
        Assert.Equal(1, s.Items);
        Assert.Equal([new LeftOutItem("logo", "Shop", LeftOutReason.NoTransfer)], s.LeftOut);
        var ready = new IarFiles(_paths, TimeProvider.System).Find(Ann)!;
        Assert.Equal(["mine"], IarArchive.Inspect(Path.Combine(_paths.IarsReady, ready.File), 1 << 20, 10).Items.Select(i => i.Name));
        Assert.Empty(Directory.GetFiles(_paths.IarsSaving));
        Assert.Equal(_paths.IarsSaving, Path.GetDirectoryName(Assert.Single(_grid.IarJobs).File));
        Assert.Equal([(Ann, "Ann Lee")], _notify.Sent);
        Assert.Contains(("iar-download-ready", "Ann Lee: 1 items, 1 left out"), Audit());
        Assert.Contains(("iar-download-asked", "Ann Lee"), Audit());
    }

    [Fact]
    public async Task Download_too_big_after_filtering_is_refused_and_deleted()
    {
        _iar = _iar with { MaxDownloadMb = 1 };
        _grid.OnIarJob = (r, _, _) =>
        {
            var b = new IarBuilder();
            var big = Guid.NewGuid();
            b.Asset(big, "texture.jp2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(2 << 20));
            b.Item(b.Folder("Pics"), "huge", big, 0, 0);
            b.Save(r.File);
            return Task.FromResult(1);
        };
        var jobs = Jobs();
        jobs.TryStartDownload(Ann, "Ann", "Lee", "stamp", null);
        var s = await Done(jobs, Ann);
        Assert.Equal(IarJobs.DownloadTooBig, s.ErrorKey);
        Assert.Empty(Directory.GetFiles(_paths.IarsSaving));
        Assert.Empty(Directory.GetFiles(_paths.IarsReady));
        Assert.Equal(1, Used(IarJobKind.Save)); // kept: each try is a full save, so it isn't free (final review, Important 4)
    }

    [Fact]
    public async Task Upload_always_loads_a_rewritten_copy_never_the_raw_file()
    {
        string? loaded = null;
        _grid.OnIarJob = (r, _, _) => { loaded = r.File; return Task.FromResult(1); };
        var raw = Upload(b => b.Notecard("Notes", "n", "x"));
        var jobs = Jobs();
        jobs.TryStartUpload(Ann, "Ann", "Lee", raw, null);
        Assert.Null((await Done(jobs, Ann)).ErrorKey);
        Assert.NotNull(loaded);
        Assert.NotEqual(raw, loaded);
    }

    [Fact]
    public void Begin_upload_reserves_the_slot_and_the_count_before_any_byte_arrives()
    {
        var jobs = Jobs();
        Assert.Null(jobs.BeginUpload(Ann, "Ann", "Lee", null));
        Assert.Equal(IarRefusal.Busy, jobs.BeginUpload(Ann, "Ann", "Lee", null));
        Assert.Equal(IarRefusal.GridBusy, jobs.BeginUpload(Guid.NewGuid(), "Bob", "Lee", null));
        Assert.Equal(1, Used(IarJobKind.Load));
        jobs.AbandonUpload(Ann, "Ann Lee", IarUploadProblem.Stopped, null); // the browser went away: the count comes back
        Assert.Equal(0, Used(IarJobKind.Load));
        Assert.Null(jobs.BeginUpload(Ann, "Ann", "Lee", null));
        jobs.AbandonUpload(Ann, "Ann Lee", IarUploadProblem.TooBig, null); // the file was too big: it doesn't
        Assert.Equal(1, Used(IarJobKind.Load));
        Assert.False(jobs.For(Ann)!.Running);
    }

    [Fact]
    public void An_upload_needs_room_for_the_largest_file_on_top_of_the_free_disk_floor()
    {
        _free = (5L << 30) + (100L << 20); // 5 GB floor + 100 MB, below 5 GB + the 500 MB largest upload
        Assert.Equal(IarRefusal.LowDisk, Jobs().BeginUpload(Ann, "Ann", "Lee", null));
        Assert.Null(Jobs().Check(Ann, IarJobKind.Save));
    }

    [Fact]
    public async Task Grid_failure_gives_the_count_back()
    {
        _grid.OnIarJob = (_, _, _) => throw new InvalidOperationException(IarJobErrors.NoRegion);
        var jobs = Jobs();
        jobs.TryStartDownload(Ann, "Ann", "Lee", "stamp", null);
        var s = await Done(jobs, Ann);
        Assert.Equal(IarJobs.NoRegion, s.ErrorKey);
        Assert.Equal(0, Used(IarJobKind.Save));
        Assert.Contains(Audit(), a => a.Action == "iar-failed");
    }

    [Fact]
    public async Task Bad_file_keeps_the_count()
    {
        var file = Path.Combine(_paths.IarsIncoming, "junk.iar");
        File.WriteAllText(file, "not an archive");
        var jobs = Jobs();
        Assert.Null(jobs.TryStartUpload(Ann, "Ann", "Lee", file, null));
        var s = await Done(jobs, Ann);
        Assert.Equal(IarJobs.NotInventory, s.ErrorKey);
        Assert.Equal(1, Used(IarJobKind.Load));
        Assert.Empty(_grid.IarJobs);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Upload_not_an_inventory_archive_refused()
    {
        var file = Upload(b => b.Raw("objects/x.xml", [1]));
        var jobs = Jobs();
        jobs.TryStartUpload(Ann, "Ann", "Lee", file, null);
        Assert.Equal(IarJobs.NotInventory, (await Done(jobs, Ann)).ErrorKey);
    }

    [Fact]
    public async Task Upload_over_the_unpacked_limit_is_refused()
    {
        _iar = _iar with { MaxUploadMb = 1, UnpackFactor = 1 };
        var file = Upload(b => b.Asset(Guid.NewGuid(), "texture.jp2", new byte[2 << 20]));
        var jobs = Jobs();
        jobs.TryStartUpload(Ann, "Ann", "Lee", file, null);
        Assert.Equal(IarJobs.TooBig, (await Done(jobs, Ann)).ErrorKey);
        Assert.Empty(_grid.IarJobs);
    }

    [Fact]
    public async Task Upload_with_banned_item_removes_it_notes_case_and_loads_the_rest()
    {
        var asset = Guid.NewGuid();
        var holder = Guid.NewGuid();
        var row = new InventoryItemRow(asset.ToString(), 0, "Logo", "", 1, 2, 0, holder.ToString(), 3, 4, 5, 0, 6, Guid.Empty.ToString(), 0, 7,
            Guid.NewGuid(), holder, Guid.NewGuid(), 8);
        var c = _takedowns.Open(new TakedownNotice(TakedownReason.Copyright, "Rita", null, null, null, new DateOnly(2026, 11, 1), "x"), true,
            n => TakedownStoreTests.Build(n, holder) with
            {
                Items = [new CaseItem(row.ItemId, holder, "Logo", null, 0, holder.ToString(), "H", "My Inventory", asset.ToString(), row, null, null, null, DateTimeOffset.UtcNow, null)],
            });
        string? loaded = null;
        _grid.OnIarJob = (r, _, _) => { loaded = IarArchive.Inspect(r.File, 1 << 20, 10).Items.Single().Name; return Task.FromResult(1); };
        var file = Upload(b => { b.Texture("Pics", "stolen logo", asset: asset); b.Notecard("Notes", "mine", "x"); });

        var jobs = Jobs();
        jobs.TryStartUpload(Ann, "Ann", "Lee", file, null);
        var s = await Done(jobs, Ann);

        Assert.Null(s.ErrorKey);
        Assert.Equal((1, 1), (s.Items, s.BannedRemoved));
        Assert.Equal("mine", loaded);
        Assert.Equal(TakedownCase.UploadMatch, _takedowns.Get(c.Number)!.Events[^1].What);
        Assert.Contains(("iar-banned-removed", "Ann Lee: 1 items"), Audit());
        Assert.Empty(Directory.GetFiles(_paths.IarsIncoming));
    }

    [Fact]
    public async Task Upload_creates_the_folder_then_loads_into_it()
    {
        var jobs = Jobs();
        jobs.TryStartUpload(Ann, "Ann", "Lee", Upload(b => b.Notecard("Notes", "n", "x")), null);
        var s = await Done(jobs, Ann);
        var request = Assert.Single(_grid.IarJobs);
        Assert.Equal(IarJobKind.Load, request.Kind);
        Assert.Equal(IarFolderNames.Imported(DateTimeOffset.UtcNow), request.Folder);
        Assert.Equal(request.Folder, s.Folder);
        Assert.Contains(await _inventory.FoldersAsync(Ann, Ct), f => f.Name == request.Folder && f.Type == -1);
        Assert.Contains(Audit(), a => a.Action == "iar-upload-loaded" && a.Target == $"Ann Lee: 1 items into {request.Folder}");
    }

    [Fact]
    public async Task Second_upload_same_day_goes_to_numbered_folder()
    {
        var jobs = Jobs();
        jobs.TryStartUpload(Ann, "Ann", "Lee", Upload(b => b.Notecard("Notes", "n", "x")), null);
        await Done(jobs, Ann);
        jobs.TryStartUpload(Ann, "Ann", "Lee", Upload(b => b.Notecard("Notes", "n", "x")), null);
        await Done(jobs, Ann);
        Assert.Equal([IarFolderNames.Imported(DateTimeOffset.UtcNow), IarFolderNames.Imported(DateTimeOffset.UtcNow) + " (2)"], _grid.IarJobs.Select(j => j.Folder));
    }

    [Fact]
    public async Task Staging_files_deleted_after_success_and_failure()
    {
        var jobs = Jobs();
        jobs.TryStartUpload(Ann, "Ann", "Lee", Upload(b => b.Notecard("Notes", "n", "x")), null);
        await Done(jobs, Ann);
        _grid.OnIarJob = (_, _, _) => throw new InvalidOperationException(IarJobErrors.Failed);
        jobs.TryStartUpload(Ann, "Ann", "Lee", Upload(b => b.Notecard("Notes", "n", "x")), null);
        await Done(jobs, Ann);
        Assert.Empty(Directory.GetFiles(_paths.IarsIncoming));
        Assert.Empty(Directory.GetFiles(_paths.IarsSaving));
    }

    [Fact]
    public async Task Ready_email_sent_only_when_email_is_set_up()
    {
        _notify.Ready = false;
        var jobs = Jobs();
        jobs.TryStartDownload(Ann, "Ann", "Lee", "stamp", null);
        Assert.Null((await Done(jobs, Ann)).ErrorKey);
        Assert.Empty(_notify.Sent);
    }

    [Fact]
    public async Task Jobs_at_once_cap_refuses_GridBusy()
    {
        var gate = new TaskCompletionSource();
        _grid.OnIarJob = async (r, _, _) => { await gate.Task; FakeGridController.IarBuilderWrite(r.File); return 1; };
        var jobs = Jobs();
        var bob = Guid.NewGuid();
        Assert.Null(jobs.TryStartDownload(Ann, "Ann", "Lee", "s", null));
        Assert.Equal(IarRefusal.GridBusy, jobs.TryStartDownload(bob, "Bob", "Lee", "s", null));
        Assert.Contains(("iar-refused", "Bob Lee: GridBusy"), Audit());
        gate.SetResult();
        await Done(jobs, Ann);
    }

    [Fact]
    public void Switched_off_resident_and_low_disk_are_refused()
    {
        var rights = new IarRights(Path.Combine(_paths.Guests, "iar-rights.json"));
        rights.Set(IarPersonRights.Default(Ann) with { UploadOff = true });
        var jobs = Jobs(rights);
        Assert.Equal(IarRefusal.PersonOff, jobs.TryStartUpload(Ann, "Ann", "Lee", Upload(b => b.Notecard("N", "n", "x")), null));
        _free = 1;
        Assert.Equal(IarRefusal.LowDisk, jobs.TryStartDownload(Ann, "Ann", "Lee", "s", null));
        Assert.Empty(Directory.GetFiles(_paths.IarsIncoming)); // a refused upload's file is removed
    }
}
