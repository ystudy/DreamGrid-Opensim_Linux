using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Api;

/// <summary>
/// The port-8001 command API (spec 5, <c>API.txt</c>): <c>RegionStatus</c> and <c>regionlist</c> need no password,
/// every process command needs level 200; module commands are appended. Replies are "ACK", data, or "NAK reason".
/// <paramref name="settings"/> is read per request, so a changed MachineId or region is seen at once.
/// </summary>
public sealed class LegacyCommandApi(
    IGridController grid,
    IRobustClient robust,
    Func<LauncherSettings> settings,
    IEnumerable<IFeatureModule> modules,
    ILogger<LegacyCommandApi> log)
{
    public const int MachineLevel = 1000;
    private const int AdminLevel = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, LegacyCommand> _commands = BuildCommands(grid, settings, modules);

    public async Task<string> HandleAsync(LegacyRequest r, CancellationToken ct)
    {
        if (!_commands.TryGetValue(r.Command, out var command))
            return $"NAK Unknown command {r.Command}";

        if (command.MinLevel > 0 && await LevelAsync(r, ct) < command.MinLevel)
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

        var parts = (r.Avatar ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return 0;
        try
        {
            var account = await robust.GetAccountAsync(parts[0], parts[1], ct);
            if (account is null) return 0;
            return await robust.AuthenticateAsync(account.PrincipalId, r.Password, ct) ? account.UserLevel : 0;
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

        Func<LegacyRequest, CancellationToken, Task<string>> RegionOp(Func<IGridController, CancellationToken, Task> op) =>
            async (r, ct) =>
            {
                if (TargetRegion(r, settings()) is { } nak) return nak;
                await op(grid, ct);
                return "ACK";
            };

        var builtIn = new LegacyCommand[]
        {
            new("RegionStatus", 0, (r, _) => Task.FromResult(TargetRegion(r, settings())
                ?? (grid.GetStatus().Region?.State.ToString() ?? "Stopped"))),
            new("regionlist", 0, (_, _) => Task.FromResult(RegionList(grid, settings()))),
            new("StartRegion", AdminLevel, RegionOp((g, ct) => g.StartComponentAsync("region", ct))),
            new("StopRegion", AdminLevel, RegionOp((g, ct) => g.StopComponentAsync("region", ct))),
            new("RestartRegion", AdminLevel, RegionOp((g, ct) => g.RestartComponentAsync("region", ct))),
            new("StartRobust", AdminLevel, Component((g, ct) => g.StartComponentAsync("robust", ct))),
            new("StopRobust", AdminLevel, Component((g, ct) => g.StopComponentAsync("robust", ct))),
            new("StartMysql", AdminLevel, Component((g, ct) => g.StartComponentAsync("database", ct))),
            new("StopMysql", AdminLevel, Component((g, ct) => g.StopComponentAsync("database", ct))),
        };

        var commands = new Dictionary<string, LegacyCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in builtIn.Concat(modules.SelectMany(m => m.Commands)))
            commands.TryAdd(c.Name, c); // a module cannot replace a built-in command
        return commands;
    }

    /// <summary>Null when the request names the grid's region (or no region); otherwise the NAK reply.</summary>
    private static string? TargetRegion(LegacyRequest r, LauncherSettings s)
    {
        if (s.Region is not { } region) return "NAK No region has been created yet.";
        if (!string.IsNullOrEmpty(r.RegionName) && !r.RegionName.Equals(region.Name, StringComparison.OrdinalIgnoreCase))
            return $"NAK Unknown region {r.RegionName}";
        if (r.RegionUuid is { } uuid && uuid != region.Uuid)
            return $"NAK Unknown region {uuid}";
        return null;
    }

    private static string RegionList(IGridController grid, LauncherSettings s)
    {
        var regions = s.Region is { } r
            ? new[] { new RegionEntry(r.Name, r.Uuid.ToString(), grid.GetStatus().Region?.State.ToString() ?? "Stopped") }
            : [];
        return JsonSerializer.Serialize(new { regions }, Json);
    }

    private sealed record RegionEntry(string Name, string Uuid, string Status);
}
