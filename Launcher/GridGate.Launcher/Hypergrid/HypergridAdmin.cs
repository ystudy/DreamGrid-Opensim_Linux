using System.Globalization;
using System.Security.Claims;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Hypergrid;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Security;

namespace GridGate.Launcher.Hypergrid;

/// <summary>What a hypergrid action did: done or not, and what to tell staff.</summary>
public sealed record HyperlinkOutcome(bool Done, string Message);

/// <summary>
/// The staff's hypergrid link actions behind <c>/hypergrid</c> (spec 2026-11-05-hypergrid-links-estates-design): the address and
/// map checks, the link test (<see cref="IGridProbe"/>), Robust's <c>link-region</c>, and removing a link. Each change is audited.
/// Adds run one at a time, so two can't take the same free cell. Callers check who may act (the page is for staff).
/// </summary>
public sealed class HypergridAdmin(IGridHyperlinks links, IGridProbe probe, IGridController grid, Func<LauncherSettings> settings, AuditLog audit,
    ILogger<HypergridAdmin> log)
{
    public const string DatabaseDown = "The grid's database isn't answering. Start the world, then try again.";
    public const string RobustDown = "The grid service isn't running. Start the world, then try again.";
    public const string HypergridOff = "Hypergrid is off, so links can't be added. Turn it on in Settings, Plan.";
    public const string HypergridOffAskStaff = "Hypergrid is off, so links can't be added. Ask the owner or an admin to turn it on.";

    /// <summary>How long to look for a link on the map when Robust's answer didn't come in time.</summary>
    public static readonly TimeSpan MapWait = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Whether links can be added now: this computer runs Robust and hypergrid is on.</summary>
    public bool CanAdd => PlanCatalog.RunsRobust(settings()) && PlanCatalog.HypergridOn(settings());

    public async Task<HyperlinkOutcome> AddAsync(ClaimsPrincipal actor, string? ip, string? typed, string? typedX, string? typedY, CancellationToken ct)
    {
        if (!CanAdd) return new(false, HypergridOff);
        if (HyperlinkAddress.Parse(typed, out var problem) is not { } address) return new(false, problem!);
        var s = settings();
        var (ownBase, ownPort) = GridEndpoints.Advertised(s);
        if (address.IsSameGrid(ownBase, ownPort, s.Hostname))
            return new(false, "That is this grid's own address. Links are for regions on other grids.");
        if (!TryCell(typedX, typedY, out var cell, out var cellProblem)) return new(false, cellProblem!);

        var check = await probe.CheckAsync(address.Gatekeeper, ct);
        if (!check.Ok) return new(false, check.Message);

        await _gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<MapSpace> map;
            try { map = await links.MapAsync(ct); }
            catch (HyperlinksUnavailableException) { return new(false, DatabaseDown); }
            var (x, y) = cell ?? HyperlinkPlace.Free(map);
            if (!HyperlinkPlace.IsFree(map, x, y)) return new(false, $"Something is already at {x}, {y} on the map. Pick another place, or leave X and Y empty.");

            HyperlinkResult result;
            try { result = await grid.LinkHypergridAsync(x, y, address, ct); }
            catch (HttpRequestException) { return new(false, RobustDown); }
            catch (InvalidOperationException e) { return new(false, e.Message); }

            var where = string.Create(CultureInfo.InvariantCulture, $"{x},{y}");
            var label = address.Region is null ? $"{check.GridName}'s default region" : $"{address.Region} on {check.GridName}";
            switch (result.Linked)
            {
                case false:
                    log.LogInformation("Hypergrid: Robust refused {Address}: {Reason}", address.Url, result.Reason);
                    return new(false, $"The grid service couldn't link {label}: {result.Reason}");
                case null when !await AppearedAsync(x, y, ct):
                    return new(false, $"The grid service didn't say whether {label} was linked. Look at the list in a minute, or try again.");
            }
            Write(actor, ip, "hyperlink-added", $"{LogText.Clean(address.Region ?? "(default)", 64)} {address.Url} {where}");
            return new(true, $"Linked {label} at {x}, {y} on the map.");
        }
        finally { _gate.Release(); }
    }

    public async Task<HyperlinkOutcome> RemoveAsync(ClaimsPrincipal actor, string? ip, Guid id, CancellationToken ct)
    {
        try
        {
            var link = (await links.ListAsync(ct)).FirstOrDefault(l => l.Id == id);
            if (link is null || !await links.RemoveAsync(id, ct)) return new(false, "That link isn't there any more.");
            Write(actor, ip, "hyperlink-removed", $"{LogText.Clean(link.Name, 64)} {LogText.Clean(link.Address, 200)}");
            return new(true, $"Removed the link to {link.Name}. Viewers that showed it on the map stop showing it when the map is opened again.");
        }
        catch (HyperlinksUnavailableException)
        {
            return new(false, DatabaseDown);
        }
    }

    /// <summary>The link test on an existing link's address.</summary>
    public async Task<HyperlinkOutcome> CheckAsync(Guid id, CancellationToken ct)
    {
        Hyperlink? link;
        try { link = (await links.ListAsync(ct)).FirstOrDefault(l => l.Id == id); }
        catch (HyperlinksUnavailableException) { return new(false, DatabaseDown); }
        if (link is null) return new(false, "That link isn't there any more.");
        if (!Uri.TryCreate(link.Address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new(false, $"The link to {link.Name} has no address that can be checked.");
        var result = await probe.CheckAsync(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), ct);
        return new(result.Ok, result.Message);
    }

    private static bool TryCell(string? typedX, string? typedY, out (int X, int Y)? cell, out string? problem)
    {
        cell = null;
        problem = null;
        var (tx, ty) = (typedX?.Trim() ?? "", typedY?.Trim() ?? "");
        if (tx.Length == 0 && ty.Length == 0) return true;
        if (tx.Length == 0 || ty.Length == 0)
        {
            problem = "Type both X and Y, or leave both empty.";
            return false;
        }
        if (!int.TryParse(tx, NumberStyles.None, CultureInfo.InvariantCulture, out var x) || !int.TryParse(ty, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            || x > HyperlinkPlace.MaxCoordinate || y > HyperlinkPlace.MaxCoordinate)
        {
            problem = $"X and Y are whole numbers from 0 to {HyperlinkPlace.MaxCoordinate}.";
            return false;
        }
        cell = (x, y);
        return true;
    }

    /// <summary>Whether a link shows up at the cell within <see cref="MapWait"/> (Robust can answer after its console went quiet).</summary>
    private async Task<bool> AppearedAsync(int x, int y, CancellationToken ct)
    {
        var until = DateTime.UtcNow + MapWait;
        while (true)
        {
            try
            {
                if ((await links.ListAsync(ct)).Any(l => l.X == x && l.Y == y)) return true;
            }
            catch (HyperlinksUnavailableException) { }
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private void Write(ClaimsPrincipal actor, string? ip, string action, string target) =>
        audit.Write(actor, ip, action, target);
}
