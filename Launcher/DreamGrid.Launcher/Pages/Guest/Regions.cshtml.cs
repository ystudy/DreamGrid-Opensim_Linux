using System.Globalization;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Endpoints;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>One region a guest has rights over, as the page shows it.</summary>
public sealed record OwnedRegionCard(Guid Id, string Name, string State, string PeopleHere, string LastStarted, RestartOffer Restart);

/// <summary>
/// The regions a guest has rights over (<c>AuthSchemes.GuestPolicy</c>, by the /Guest folder convention), with a restart for one
/// that is stuck. The restart re-checks the person's rights and <see cref="RegionOwnerRules"/> on the server, whatever the form said.
/// </summary>
public sealed class MyRegionsModel(RegionRights rights, IGridController grid, IOnlineUsers online, RegionActions actions,
    RegionRestarts restarts, AuditLog audit, TimeProvider time) : PageModel
{
    public IReadOnlyList<OwnedRegionCard> Regions { get; private set; } = [];

    [TempData] public string? Notice { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var owner = Owner();
        var status = grid.GetStatus();
        var people = await online.ListAsync(ct);
        var now = time.GetUtcNow();
        var cards = new List<OwnedRegionCard>();
        foreach (var right in rights.OwnedBy(owner))
        {
            var region = status.Regions.FirstOrDefault(r => r.Id == right.Region);
            if (region is null) continue;
            cards.Add(new OwnedRegionCard(region.Id, region.Name, StateWords(region.State),
                people.Available ? people.Avatars.Count(a => a.RegionId == region.Id).ToString(CultureInfo.InvariantCulture) : "unknown",
                LastStarted(status, region, now),
                RegionOwnerRules.Restart(status, region, owner, OwnerOf, restarts.Last(region.Id), now)));
        }
        Regions = cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IActionResult OnPostRestart(Guid id)
    {
        var owner = Owner();
        var status = grid.GetStatus();
        var region = status.Regions.FirstOrDefault(r => r.Id == id);

        // Only a region the person holds the rights to; anything else is a crafted post.
        if (region is null || OwnerOf(id) != owner)
        {
            Write(owner, region?.Name ?? "(unknown region)", "refused");
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var now = time.GetUtcNow();
        var offer = RestartOffer.None;
        var accepted = restarts.TryBegin(id, now, last =>
        {
            offer = RegionOwnerRules.Restart(status, region, owner, OwnerOf, last, now);
            return offer is { Shown: true, Allowed: true };
        });
        if (!accepted)
        {
            Write(owner, region.Name, "refused");
            Notice = offer.Reason ?? $"{region.Name} doesn't need a restart.";
            return Redirect("/guest/regions");
        }

        if (!actions.TryRun(id, offer.Action!.Value)) return NotFound();
        Write(owner, region.Name, "ok");
        Notice = $"Restarting {region.Name}. It can take a minute or two.";
        return Redirect("/guest/regions");
    }

    /// <summary>Who holds the rights to the region now; a right being taken away holds nothing.</summary>
    private Guid? OwnerOf(Guid region) => rights.Of(region) is { Held: true } r ? r.Owner : null;

    /// <summary>The guest's id; the page is behind the guest policy, so the cookie's claims are there.</summary>
    private Guid Owner() => GuestSignIn.Read(User)!.Value.Principal;

    private void Write(Guid owner, string target, string result) =>
        audit.Write(new AuditEntry(User.Identity?.Name ?? "", owner, AuthSchemes.GuestRole,
            HttpContext.Connection.RemoteIpAddress?.ToString(), "region-restart", target, result));

    private static string StateWords(RegionState state) => state switch
    {
        RegionState.Running => "running",
        RegionState.Stopped => "stopped",
        RegionState.Starting => "starting",
        RegionState.Stopping => "stopping",
        RegionState.Failed => "not responding",
        _ => "parked",
    };

    /// <summary>When the region's program started (UTC), or "not running".</summary>
    private static string LastStarted(GridStatus status, RegionStatus region, DateTimeOffset now)
    {
        var process = status.Programs.FirstOrDefault(p => p.Regions.Contains(region.Id))?.Process;
        if (region.State is not (RegionState.Running or RegionState.Starting) || process?.Uptime is not { } uptime) return "not running";
        return "Last started " + (now - uptime).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (UTC)";
    }
}
