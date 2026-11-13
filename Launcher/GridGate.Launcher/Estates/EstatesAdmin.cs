using System.Security.Claims;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Estates;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Security;

namespace GridGate.Launcher.Estates;

/// <summary>What an estate action did: done or not, and what to tell the admin.</summary>
public sealed record EstateOutcome(bool Done, string Message, int? Estate = null);

/// <summary>What kind of estate it is, for what may be changed (spec 2026-11-05-hypergrid-links-estates-design, protected estates).</summary>
public enum EstateKind { Plain, Grid, RegionOwner }

/// <summary>
/// The admin's estate actions behind the Estates pages (spec 2026-11-05-hypergrid-links-estates-design). Rename, owner, managers and
/// delete are written to the estate tables (<see cref="IGridEstates"/>), then every running region program is told to read the
/// estate again; a new estate and a region's move go through the region console. Each change is audited. Changes run one at a
/// time. Callers check who may act (the pages are admin-only).
/// </summary>
public sealed class EstatesAdmin(IGridEstates estates, IEstateRemover remover, IGridController grid, RegionRights rights, Func<LauncherSettings> settings,
    AuditLog audit, ILogger<EstatesAdmin> log)
{
    public const string DatabaseDown = "The regions' database isn't answering. Start the world, then try again.";
    public const string NoEstate = "That estate isn't there any more.";
    public const int MaxName = 64;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The name of the estate the grid's own regions are in (OpenSim's DefaultEstateName).</summary>
    public string GridEstateName => $"{settings().GridName} Estate";

    public EstateKind KindOf(int id, string name) =>
        string.Equals(name, GridEstateName, StringComparison.OrdinalIgnoreCase) ? EstateKind.Grid
        : rights.OwnerEstates().Any(e => e.EstateId == id) ? EstateKind.RegionOwner
        : EstateKind.Plain;

    /// <summary>The person an estate was made for, when it is a region owner's.</summary>
    public string? MadeFor(int id) => rights.OwnerEstates().FirstOrDefault(e => e.EstateId == id) is { } e
        ? rights.OwnedBy(e.Owner).FirstOrDefault()?.OwnerName ?? e.Owner.ToString()
        : null;

    /// <summary>
    /// The plain-words problem with an estate name, or null: 1–64 characters, words with single spaces, no double quotes, no
    /// word starting with "--" (the console splits on spaces and would read those as options).
    /// </summary>
    public static string? NameProblem(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0) return "Type an estate name.";
        if (n.Length > MaxName) return $"An estate name can be at most {MaxName} characters.";
        if (n.Any(char.IsControl)) return "An estate name can't contain line breaks or tabs.";
        if (n.Split(' ').Any(w => w.Length == 0 || w.Any(char.IsWhiteSpace))) return "Use single spaces between the words of an estate name.";
        if (n.Contains('"')) return "An estate name can't hold double quotes.";
        if (n.Split(' ').Any(w => w.StartsWith("--", StringComparison.Ordinal))) return "A word in an estate name can't start with \"--\".";
        return null;
    }

    public Task<EstateOutcome> RenameAsync(ClaimsPrincipal actor, string? ip, int id, string? name, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (NameProblem(name) is { } bad) return new(false, bad);
            var newName = name!.Trim();
            if (await estates.GetAsync(id, ct) is not { } e) return new(false, NoEstate);
            if (KindOf(id, e.Name) == EstateKind.Grid)
                return new(false, "The grid's own estate keeps its name: new regions join the estate of that name.");
            if (string.Equals(newName, GridEstateName, StringComparison.OrdinalIgnoreCase))
                return new(false, "That name is the grid's own estate's.");
            if (newName == e.Name) return new(true, "Saved.", id);
            switch (await estates.RenameAsync(id, newName, ct))
            {
                case EstateChange.NoEstate: return new(false, NoEstate);
                case EstateChange.NameTaken: return new(false, $"Another estate is already called {LogText.Clean(newName, MaxName)}.");
            }
            if (rights.OwnerEstates().FirstOrDefault(o => o.EstateId == id) is { } owned) rights.SetEstate(owned.Owner, id, newName);
            Write(actor, ip, "estate-renamed", $"{id} {e.Name} → {newName}");
            return new(true, $"Renamed to {newName}.{await ReloadAsync(id, ct)}", id);
        });

    public Task<EstateOutcome> SetOwnerAsync(ClaimsPrincipal actor, string? ip, int id, RobustAccount owner, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (await estates.GetAsync(id, ct) is not { } e) return new(false, NoEstate);
            switch (KindOf(id, e.Name))
            {
                case EstateKind.Grid: return new(false, "The grid's own estate always belongs to the grid owner.");
                case EstateKind.RegionOwner:
                    return new(false, $"This estate was made for {MadeFor(id)}'s regions. To give the regions to someone else, change who owns them on the region's page.");
            }
            var name = Full(owner);
            if (e.Owner == owner.PrincipalId) return new(false, $"{name} already owns {e.Name}.");
            if (await estates.SetOwnerAsync(id, owner.PrincipalId, ct) != EstateChange.Done) return new(false, NoEstate);
            Write(actor, ip, "estate-owner-changed", $"{id} {e.Name} {e.OwnerName} → {name}");
            return new(true, $"{name} owns {e.Name} now.{await ReloadAsync(id, ct)}", id);
        });

    public Task<EstateOutcome> AddManagerAsync(ClaimsPrincipal actor, string? ip, int id, RobustAccount manager, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (await estates.GetAsync(id, ct) is not { } e) return new(false, NoEstate);
            var name = Full(manager);
            if (e.Owner == manager.PrincipalId) return new(false, $"{name} owns {e.Name}; an owner can already do everything a manager can.");
            switch (await estates.AddManagerAsync(id, manager.PrincipalId, ct))
            {
                case EstateChange.NoEstate: return new(false, NoEstate);
                case EstateChange.AlreadyManager: return new(false, $"{name} is already a manager of {e.Name}.");
                case EstateChange.TooManyManagers: return new(false, $"An estate can have at most {GridEstates.MaxManagers} managers. Remove one first.");
            }
            Write(actor, ip, "estate-manager-added", $"{id} {e.Name} {name}");
            return new(true, $"{name} is a manager of {e.Name} now.{await ReloadAsync(id, ct)}", id);
        });

    public Task<EstateOutcome> RemoveManagerAsync(ClaimsPrincipal actor, string? ip, int id, Guid manager, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (await estates.GetAsync(id, ct) is not { } e) return new(false, NoEstate);
            var who = e.Managers.FirstOrDefault(m => m.Id == manager);
            if (who is null || await estates.RemoveManagerAsync(id, manager, ct) != EstateChange.Done)
                return new(false, "That person isn't a manager of this estate any more.");
            Write(actor, ip, "estate-manager-removed", $"{id} {e.Name} {who.Name}");
            return new(true, $"{who.Name} isn't a manager of {e.Name} any more.{await ReloadAsync(id, ct)}", id);
        });

    public Task<EstateOutcome> MoveRegionAsync(ClaimsPrincipal actor, string? ip, int id, Guid region, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (await estates.GetAsync(id, ct) is not { } e) return new(false, NoEstate);
            var r = settings().Regions.FirstOrDefault(x => x.Uuid == region && x.DeletedAt is null);
            if (r is null) return new(false, "That region isn't one of this computer's.");
            if (e.Regions.Contains(region)) return new(false, $"{r.Name} is already in {e.Name}.");
            if (rights.Of(region) is { } right)
                return new(false, $"{r.Name} belongs to {right.OwnerName}, so it stays in their estate. Change who owns it on the region's page.");
            try { await grid.LinkEstateAsync(region, id, ct); }
            catch (InvalidOperationException x) { return new(false, x.Message.StartsWith("Start ", StringComparison.Ordinal) ? $"{x.Message.TrimEnd('.')}: a region moves only while it runs." : $"{r.Name} didn't move. {LogText.Clean(x.Message, 300)}"); }
            catch (HttpRequestException) { return new(false, $"{r.Name}'s program didn't answer. Try again in a moment."); }
            Write(actor, ip, "estate-region-moved", $"{r.Name} → {id} {e.Name}");
            return new(true, $"Moved {r.Name} into {e.Name}.", id);
        });

    public Task<EstateOutcome> CreateAsync(ClaimsPrincipal actor, string? ip, string? name, RobustAccount owner, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (NameProblem(name) is { } bad) return new(false, bad);
            var newName = name!.Trim();
            if (await estates.NameTakenAsync(newName, null, ct)) return new(false, $"Another estate is already called {LogText.Clean(newName, MaxName)}.");
            // estate create runs on any region; the new estate has no regions until one moves in.
            var running = grid.GetStatus().Regions.FirstOrDefault(r => r.State == RegionState.Running);
            if (running is null) return new(false, "Start a region first: a new estate is made through a running region.");
            (int Id, string Name) made;
            try { made = await grid.CreateEstateAsync(running.Id, owner.PrincipalId, newName, ct); }
            catch (InvalidOperationException x) { return new(false, $"The estate wasn't made. {LogText.Clean(x.Message, 300)}"); }
            catch (HttpRequestException) { return new(false, $"{running.Name}'s program didn't answer. Try again in a moment."); }
            Write(actor, ip, "estate-created", $"{made.Id} {made.Name} {Full(owner)}");
            return new(true, $"Made {made.Name}, owned by {Full(owner)}. Move regions into it below.", made.Id);
        });

    public Task<EstateOutcome> DeleteAsync(ClaimsPrincipal actor, string? ip, int id, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (await estates.GetAsync(id, ct) is not { } e) return new(false, NoEstate);
            if (KindOf(id, e.Name) == EstateKind.Grid) return new(false, "The grid's own estate can't be deleted.");
            if (e.Regions.Count > 0) return new(false, $"{e.Name} still has {e.Regions.Count} region(s). Move them to another estate first.");
            bool removed;
            try { removed = await remover.RemoveIfEmptyAsync(id, ct); }
            catch (StaffDirectoryUnavailableException) { return new(false, DatabaseDown); }
            if (!removed) return new(false, $"A region joined {e.Name} meanwhile, so it wasn't deleted.");
            Write(actor, ip, "estate-deleted", $"{id} {e.Name}");
            return new(true, $"Deleted {e.Name}.");
        });

    /// <summary>Tells the running regions to read the estate again; the sentence to add when some didn't confirm.</summary>
    private async Task<string> ReloadAsync(int id, CancellationToken ct)
    {
        IReadOnlyList<string> missed;
        try { missed = await grid.ReloadEstateAsync(id, ct); }
        catch (Exception x) when (x is InvalidOperationException or HttpRequestException)
        {
            log.LogWarning("Estates: estate {Estate} wasn't read again: {Message}", id, x.Message);
            return " Running regions show it after they restart.";
        }
        return missed.Count == 0 ? "" : $" {string.Join(", ", missed)} didn't answer, so its regions show it after they restart.";
    }

    private async Task<EstateOutcome> GuardAsync(Func<Task<EstateOutcome>> work)
    {
        await _gate.WaitAsync();
        try { return await work(); }
        catch (EstatesUnavailableException x)
        {
            log.LogWarning("Estates: the database didn't answer: {Message}", x.InnerException?.Message ?? x.Message);
            return new(false, DatabaseDown);
        }
        finally { _gate.Release(); }
    }

    private void Write(ClaimsPrincipal actor, string? ip, string action, string target) =>
        audit.Write(actor, ip, action, LogText.Clean(target, 300));

    private static string Full(RobustAccount a) => $"{a.FirstName} {a.LastName}";
}
