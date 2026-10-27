using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// Ports for new programs and regions. A new program gets <c>HttpPort = Next(s, s.RegionPort)</c> and
/// <c>ConsolePort = Next(s, s.RegionConsolePort, {HttpPort})</c>, and its first region <c>UdpPort = HttpPort</c> (TCP and
/// UDP share the number, as version 1's 8004 does); a region joining a program gets <c>UdpPort = Next(s, s.RegionPort)</c>.
/// </summary>
public static class PortAllocator
{
    /// <summary>
    /// The fixed ports and every program's and region's, deleted regions included. The two "first port" settings are not
    /// taken themselves: a fresh install's first program gets them, as version 1 did.
    /// </summary>
    public static IReadOnlySet<int> Taken(LauncherSettings s)
    {
        var taken = new HashSet<int> { s.WebPort, s.MariaDbPort, s.RobustPublicPort, s.RobustPrivatePort, s.RobustConsolePort };
        foreach (var p in s.Programs) { taken.Add(p.HttpPort); taken.Add(p.ConsolePort); }
        foreach (var r in s.Regions) taken.Add(r.UdpPort);
        taken.Remove(0);
        return taken;
    }

    public static int Next(LauncherSettings s, int from, IReadOnlySet<int>? alsoTaken = null)
    {
        var taken = Taken(s);
        for (var port = from; port <= 65535; port++)
            if (!taken.Contains(port) && alsoTaken?.Contains(port) != true) return port;
        throw new InvalidOperationException($"No free port from {from}.");
    }

    /// <summary>Moves every program or region assignment of <paramref name="oldPort"/>; null when none uses it.</summary>
    public static LauncherSettings? Replace(LauncherSettings s, int oldPort, int newPort)
    {
        var programs = s.Programs.Select(p => p with
        {
            HttpPort = p.HttpPort == oldPort ? newPort : p.HttpPort,
            ConsolePort = p.ConsolePort == oldPort ? newPort : p.ConsolePort,
        }).ToList();
        var regions = s.Regions.Select(r => r.UdpPort == oldPort ? r with { UdpPort = newPort } : r).ToList();
        if (programs.SequenceEqual(s.Programs) && regions.SequenceEqual(s.Regions)) return null;
        return s with { Programs = programs, Regions = regions };
    }
}
