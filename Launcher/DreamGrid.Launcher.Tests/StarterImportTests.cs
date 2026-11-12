using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Starter;
using DreamGrid.Launcher.Iar;
using DreamGrid.Launcher.Starter;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Loading starter avatars from an archive into new model accounts (spec 2026-11-11-starter-avatars-design §6.2).</summary>
public sealed class StarterImportTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("dg-starterimp-").FullName;
    private readonly LauncherPaths _paths;
    private readonly FakeGridController _grid = new();
    private readonly FakeRobustAccounts _robust = new();
    private readonly FakeInventoryStore _inventory;
    private readonly FakeAccountRemover _remover;
    private readonly TakedownStore _takedowns;
    private readonly BackgroundWork _background = new(NullLogger<BackgroundWork>.Instance);
    private readonly AuditLog _audit;
    private readonly StarterAvatarList _starters;
    private static readonly IarStaffActor Owner = new("Test Owner", Guid.NewGuid(), "owner", "127.0.0.1");

    public StarterImportTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        IarFolders.Prepare(_paths);
        Directory.CreateDirectory(_paths.Logs);
        _inventory = new FakeInventoryStore(_robust);
        _remover = new FakeAccountRemover(_robust);
        _takedowns = new TakedownStore(_paths.Takedowns, TimeProvider.System, NullLogger<TakedownStore>.Instance);
        _audit = new AuditLog(Path.Combine(_paths.Logs, "audit.log"), TimeProvider.System);
        _starters = new StarterAvatarList(Path.Combine(_root, "avatars.json"), TimeProvider.System);
        // Robust's console makes the model, which the account service then reports.
        _grid.OnCreateResident = (first, last, email, level) =>
        {
            if (_robust.All.Any(a => a.FirstName == first && a.LastName == last)) throw new NameTakenException(first, last);
            var id = Guid.NewGuid();
            _robust.Add(first, last, level, "x", id, email ?? "");
            _inventory.AddDefaultFolders(id);
            return _robust.All.Single(a => a.PrincipalId == id);
        };
    }

    public void Dispose()
    {
        _background.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _audit.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private StarterImport Import() => new(_grid, _robust, _inventory, _takedowns, _starters, _remover, _paths, _background, _audit,
        TimeProvider.System, NullLogger<StarterImport>.Instance);

    private string Pending(IarBuilder b) => b.Save(Path.Combine(_paths.IarsIncoming, Guid.NewGuid().ToString("N") + ".iar"));

    private static async Task<StarterImportProgress> Done(StarterImport import, string id)
    {
        var guard = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            if (import.For(id) is { Running: false } p) return p;
            if (DateTime.UtcNow > guard) throw new TimeoutException("the import never finished");
            await Task.Delay(20, Ct);
        }
    }

    private List<string> Audit() => [.. _audit.Recent(e => e.Entry.Action.StartsWith("starter-avatar", StringComparison.Ordinal), 100)
        .Select(e => $"{e.Entry.Action} {e.Entry.Target} {e.Entry.Result}").Reverse()];

    [Fact]
    public async Task Import_makes_model_loads_folder_and_adds()
    {
        var loaded = new List<(IarJobRequest Request, List<string> Items)>();
        _grid.OnIarJob = (r, _, _) =>
        {
            lock (loaded) loaded.Add((r, [.. IarArchive.Inspect(r.File, 1 << 24, 100).Items.Select(i => i.Name)]));
            return Task.FromResult(3);
        };
        var file = Pending(StarterArchiveTests.Avatars("Lyra complete female avatar", "Professional Lady #1"));
        var import = Import();
        var id = import.Start(Owner, file, [("FULLAVATARS/Lyra complete female avatar", "Lyra"), ("FULLAVATARS/Professional Lady #1", "Lady One")]);
        var p = await Done(import, id);

        Assert.All(p.Lines, l => Assert.Null(l.Error));
        Assert.Equal(["Lyra", "Lady One"], _starters.All().Select(a => a.Name));
        Assert.Equal(["Starter01 Avatar", "Starter02 Avatar"], _starters.All().Select(a => a.ModelName));
        var lyra = _starters.All()[0];
        Assert.Equal("Lyra/FULLAVATARS/Lyra complete female avatar", lyra.OutfitFolder);
        Assert.Equal(lyra.Model, loaded[0].Request.Principal);
        Assert.Equal(IarJobKind.Load, loaded[0].Request.Kind);
        Assert.Equal("Lyra", loaded[0].Request.Folder);
        Assert.Equal("Lady One", loaded[1].Request.Folder);
        Assert.Equal(["Shape", "Shirt", "Lyra"], loaded[0].Items);
        Assert.False(File.Exists(file)); // the upload is gone once the import ends
        Assert.Equal(["starter-avatar-added Lyra ok", "starter-avatar-added Lady One ok"], Audit());
    }

    [Fact]
    public async Task Banned_item_is_left_out_and_noted()
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
        List<string>? items = null;
        _grid.OnIarJob = (r, _, _) => { items = [.. IarArchive.Inspect(r.File, 1 << 24, 100).Items.Select(i => i.Name)]; return Task.FromResult(1); };
        var b = StarterArchiveTests.Avatars("Lyra complete female avatar");
        b.Texture("FULLAVATARS/Lyra complete female avatar", "stolen logo", asset: asset);
        var import = Import();
        await Done(import, import.Start(Owner, Pending(b), [("FULLAVATARS/Lyra complete female avatar", "Lyra")]));

        Assert.DoesNotContain("stolen logo", items!);
        Assert.Equal(TakedownCase.UploadMatch, _takedowns.Get(c.Number)!.Events[^1].What);
        Assert.Equal(["starter-avatar-added Lyra 1 taken-down item left out"], Audit());
    }

    [Fact]
    public async Task Two_imports_never_share_a_model_name()
    {
        _grid.OnIarJob = async (_, _, ct) => { await Task.Delay(30, ct); return 1; };
        var import = Import();
        var a = import.Start(Owner, Pending(StarterArchiveTests.Avatars("Lyra complete female avatar")), [("FULLAVATARS/Lyra complete female avatar", "Lyra")]);
        var b = import.Start(Owner, Pending(StarterArchiveTests.Avatars("Leo complete male avatar")), [("FULLAVATARS/Leo complete male avatar", "Leo")]);
        await Done(import, a);
        await Done(import, b);
        Assert.Equal(2, _starters.All().Select(x => x.ModelFirst).Distinct().Count());
    }

    [Fact]
    public async Task A_name_already_on_the_grid_is_skipped()
    {
        _robust.Add("Starter01", "Avatar", 0, "x", Guid.NewGuid(), ""); // someone made it by hand
        _grid.OnIarJob = (_, _, _) => Task.FromResult(1);
        var import = Import();
        await Done(import, import.Start(Owner, Pending(StarterArchiveTests.Avatars("Lyra complete female avatar")), [("FULLAVATARS/Lyra complete female avatar", "Lyra")]));
        Assert.Equal("Starter02", _starters.All().Single().ModelFirst);
    }

    [Fact]
    public async Task Failed_load_deletes_its_model_and_goes_on()
    {
        _grid.OnIarJob = (r, _, _) => r.Folder == "Lyra"
            ? Task.FromException<int>(new InvalidOperationException(IarJobErrors.NoRegion))
            : Task.FromResult(1);
        var import = Import();
        var p = await Done(import, import.Start(Owner, Pending(StarterArchiveTests.Avatars("Lyra complete female avatar", "Leo complete male avatar")),
            [("FULLAVATARS/Lyra complete female avatar", "Lyra"), ("FULLAVATARS/Leo complete male avatar", "Leo")]));
        Assert.NotNull(p.Lines[0].Error);
        Assert.Null(p.Lines[1].Error);
        Assert.Equal(["Leo"], _starters.All().Select(a => a.Name));
        Assert.Single(_remover.Deleted);
        Assert.Equal("starter-avatar-added Lyra failed", Audit()[0]);
    }

    [Fact]
    public async Task Add_existing_account_needs_no_archive()
    {
        var id = Guid.NewGuid();
        _robust.Add("Mia", "Model", 0, "x", id, "");
        var import = Import();
        var a = await import.AddExistingAsync(Owner, "Mia", "Model", "Mia", Ct);
        Assert.Equal(id, a!.Model);
        Assert.Null(await import.AddExistingAsync(Owner, "No", "Body", "Nobody", Ct));
        Assert.Null(await import.AddExistingAsync(Owner, "Mia", "Model", "Mia again", Ct)); // already on the list
        Assert.Equal(["starter-avatar-added Mia ok"], Audit());
    }

    [Theory]
    [InlineData("Staff", "Person", 200, "")]   // staff (and the owner) have a level above 0
    [InlineData("Res", "Ident", 0, "res@example.com")] // a resident has an email
    [InlineData("Wait", "Ing", -1, "")]   // a sign-up still waiting
    [InlineData("Old.Name", "Here", 0, "")] // a name the console would split
    public async Task Add_existing_refuses_people_and_bad_names(string first, string last, int level, string email)
    {
        // Final review I2: a model's password is reset and its sign-in blocked, so it must never be someone's own avatar.
        _robust.Add(first, last, level, "x", Guid.NewGuid(), email);
        Assert.Null(await Import().AddExistingAsync(Owner, first, last, "Mia", Ct));
        Assert.Empty(_starters.All());
    }
}
