using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Api;

/// <summary>
/// The port-8001 command API (spec 5, <c>API.txt</c>): <c>RegionStatus</c> and <c>regionlist</c> need no password,
/// every process command needs level 200; module commands are appended. Replies are "ACK", data, or "NAK reason".
/// <paramref name="settings"/> is read per request, so a changed MachineId or region is seen at once. Avatar password
/// checks share the website's <see cref="LoginThrottle"/>: a locked name is refused without asking Robust.
/// </summary>
public sealed class LegacyCommandApi(
    IGridController grid,
    IRobustClient robust,
    Func<LauncherSettings> settings,
    IEnumerable<IFeatureModule> modules,
    LoginThrottle throttle,
    ILogger<LegacyCommandApi> log)
{
    public const int MachineLevel = 1000;
    /// <summary>A built-in command's <see cref="LegacyCommand.MinLevel"/> meaning "the admin level in settings" (200 unless changed).</summary>
    private const int AdminLevel = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, LegacyCommand> _commands = BuildCommands(grid, settings, modules);

    public async Task<string> HandleAsync(LegacyRequest r, CancellationToken ct)
    {
        if (!_commands.TryGetValue(r.Command, out var command))
            return $"NAK Unknown command {r.Command}";

        var needed = command.MinLevel == AdminLevel ? settings().Security.AdminLevel : command.MinLevel;
        if (needed > 0 && await LevelAsync(r, ct) < needed)
            return $"NAK You do not have permission to {command.Name}";

        try
        {
            return await command.Run(r, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return "NAK " + e.Message;
        }
    }

    /// <summary>1000 for the MachineId; else the level of the avatar ("First Last") whose password matches; else 0.</summary>
    private async Task<int> LevelAsync(LegacyRequest r, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.Password)) return 0;
        if (FixedEquals(r.Password, settings().MachineId)) return MachineLevel;

        var name = AvatarName.Parse(r.Avatar);
        if (!name.IsComplete) return 0;
        if (throttle.IsLocked(name.Key))
        {
            log.LogInformation("Avatar check for first name {First} refused: too many failed attempts.", name.ForLog);
            return 0;
        }
        try
        {
            var account = await robust.GetAccountAsync(name.First, name.Last, ct);
            if (account is null || !await robust.AuthenticateAsync(account.PrincipalId, r.Password, ct))
            {
                throttle.RecordFailure(name.Key);
                return 0;
            }
            throttle.RecordSuccess(name.Key);
            // A Region Server's or Connected Server's estate owner is level 0 on the other grid but administers here; on a
            // Connected Server a higher level on that grid counts for nothing.
            return AdminRule.EffectiveLevel(settings(), account);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Robust down, slow (HttpClient timeout) or answering garbage: nobody but the MachineId gets in.
            // Only the exception type is logged: messages could echo the request.
            log.LogWarning("Avatar check against Robust failed ({Error}); treating the caller as level 0.", e.GetType().Name);
            return 0;
        }
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static Dictionary<string, LegacyCommand> BuildCommands(
        IGridController grid, Func<LauncherSettings> settings, IEnumerable<IFeatureModule> modules)
    {
        Func<LegacyRequest, CancellationToken, Task<string>> Component(Func<IGridController, CancellationToken, Task> op) =>
            async (_, ct) => { await op(grid, ct); return "ACK"; };

        // The same power menu actions as the website's; StopRegion is a Stop, never a Park (the controller logs which
        // other regions an action affected, since a script sees no warning).
        Func<LegacyRequest, CancellationToken, Task<string>> RegionOp(RegionPower action) =>
            async (r, ct) =>
            {
                var (region, nak) = TargetRegion(r, settings());
                if (nak is not null) return nak;
                await grid.RegionPowerAsync(region!.Uuid, action, ct);
                return "ACK";
            };

        var builtIn = new LegacyCommand[]
        {
            new("RegionStatus", 0, (r, _) =>
            {
                var (region, nak) = TargetRegion(r, settings());
                return Task.FromResult(nak ?? StateOf(grid, region!));
            }),
            new("regionlist", 0, (_, _) => Task.FromResult(RegionList(grid, settings()))),
            new("StartRegion", AdminLevel, RegionOp(RegionPower.Start)),
            new("StopRegion", AdminLevel, RegionOp(RegionPower.Stop)),
            new("RestartRegion", AdminLevel, RegionOp(RegionPower.Restart)),
            new("StartRobust", AdminLevel, Component((g, ct) => g.StartComponentAsync("robust", ct))),
            new("StopRobust", AdminLevel, Component((g, ct) => g.StopComponentAsync("robust", ct))),
            // A pending upgrade has already stopped any leftover Robust and region before the database is started.
            new("StartMysql", AdminLevel, Component((g, ct) => g.StartComponentAsync("database", ct))),
            new("StopMysql", AdminLevel, Component((g, ct) => g.StopComponentAsync("database", ct))),
        };

        var commands = new Dictionary<string, LegacyCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in builtIn.Concat(modules.SelectMany(m => m.Commands)))
            commands.TryAdd(c.Name, c); // a module cannot replace a built-in command
        return commands;
    }

    /// <summary>
    /// The live region the request names (by name ignoring case, or by UUID); with neither, the only region. Otherwise
    /// the NAK reply.
    /// </summary>
    private static (RegionSettings? Region, string? Nak) TargetRegion(LegacyRequest r, LauncherSettings s)
    {
        var live = RegionModel.Live(s).ToList();
        if (live.Count == 0) return (null, "NAK No region has been created yet.");
        if (!string.IsNullOrEmpty(r.RegionName))
            return live.FirstOrDefault(x => x.Name.Equals(r.RegionName, StringComparison.OrdinalIgnoreCase)) is { } named
                ? (named, null) : (null, $"NAK Unknown region {r.RegionName}");
        if (r.RegionUuid is { } uuid)
            return live.FirstOrDefault(x => x.Uuid == uuid) is { } byId ? (byId, null) : (null, $"NAK Unknown region {uuid}");
        return live.Count == 1 ? (live[0], null) : (null, "NAK Name the region.");
    }

    private static string StateOf(IGridController grid, RegionSettings region) =>
        grid.GetStatus().Regions.FirstOrDefault(x => x.Id == region.Uuid)?.State.ToString() ?? "Stopped";

    private static string RegionList(IGridController grid, LauncherSettings s)
    {
        var regions = RegionModel.Live(s).Select(r => new RegionEntry(r.Name, r.Uuid.ToString(), StateOf(grid, r))).ToList();
        return JsonSerializer.Serialize(new { regions }, Json);
    }

    private sealed record RegionEntry(string Name, string Uuid, string Status);
}
