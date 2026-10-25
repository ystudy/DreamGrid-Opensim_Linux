using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>A port the grid needs is held by another program; the message names the port and its use.</summary>
/// <summary><see cref="Port"/> is the port that is taken, when known.</summary>
public sealed class PortConflictException(string message, int? port = null) : Exception(message)
{
    public int? Port { get; } = port;
}

/// <summary>Finds a grid port already taken by another program, before anything is started.</summary>
public static class PortCheck
{
    private static readonly IReadOnlySet<int> NoPorts = new HashSet<int>();

    public static string? FindConflict(LauncherSettings s) => FindConflict(s, NoPorts);

    /// <summary>As <see cref="FindConflict(LauncherSettings)"/>, ignoring <paramref name="skip"/> (ports of children about to be adopted).</summary>
    public static string? FindConflict(LauncherSettings s, IReadOnlySet<int> skip) => FindConflictPort(s, skip)?.Message;

    /// <summary>As <see cref="FindConflict(LauncherSettings, IReadOnlySet{int})"/>, with the port that is taken.</summary>
    public static (int Port, string Message)? FindConflictPort(LauncherSettings s, IReadOnlySet<int> skip)
    {
        var checks = new (int Port, string Name, IPAddress Address, bool Udp)[]
        {
            (s.MariaDbPort, "MariaDB", IPAddress.Loopback, false),
            (s.RobustPublicPort, "Robust public", IPAddress.Any, false),
            (s.RobustPrivatePort, "Robust private", IPAddress.Any, false),
            (s.RegionPort, "Region", IPAddress.Any, false),
            (s.RegionPort, "Region UDP", IPAddress.Any, true),
            (s.RobustConsolePort, "Robust console", IPAddress.Any, false),
            (s.RegionConsolePort, "Region console", IPAddress.Any, false),
            (s.WebPort, "Web", IPAddress.Any, false),
        };
        foreach (var (port, name, address, udp) in checks)
        {
            if (skip.Contains(port)) continue;
            // On Windows a wildcard bind succeeds next to another program's 127.0.0.1 listener, which would then
            // take the loopback traffic: probe loopback as well.
            if (!CanBind(address, port, udp) || (address.Equals(IPAddress.Any) && !CanBind(IPAddress.Loopback, port, udp)))
                return (port, $"Port {port} ({name}) is already in use by another program.");
        }
        return null;
    }

    private static bool CanBind(IPAddress address, int port, bool udp)
    {
        try
        {
            if (udp)
            {
                using var u = new UdpClient(new IPEndPoint(address, port));
            }
            else
            {
                var l = new TcpListener(address, port);
                l.Start();
                l.Stop();
            }
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
