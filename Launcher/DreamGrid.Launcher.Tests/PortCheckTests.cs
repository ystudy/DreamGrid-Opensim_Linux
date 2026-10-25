using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class PortCheckTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Settings whose ports are all free (distinct ephemeral ports).</summary>
    private static LauncherSettings FreeSettings()
    {
        var ports = new HashSet<int>();
        while (ports.Count < 8) ports.Add(FreePort());
        var p = ports.ToArray();
        return new LauncherSettings
        {
            Db = new DatabaseSecrets("r", "a", "b"),
            Console = new ConsoleSecrets("u", "p"),
            MachineId = "m",
            WebPort = p[0], RobustPublicPort = p[1], RobustPrivatePort = p[2], RegionPort = p[3],
            MariaDbPort = p[4], RobustConsolePort = p[5], RegionConsolePort = p[6],
        };
    }

    [Fact]
    public void PortCheck_reports_first_busy_port_by_name()
    {
        var s = FreeSettings();
        var holder = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        holder.Start();
        try
        {
            Assert.Equal($"Port {s.RobustPublicPort} (Robust public) is already in use by another program.",
                PortCheck.FindConflict(s));
        }
        finally { holder.Stop(); }
    }

    [Fact]
    public void PortCheck_is_null_when_every_port_is_free()
    {
        Assert.Null(PortCheck.FindConflict(FreeSettings()));
    }

    [Fact]
    public void PortCheck_reports_busy_udp_region_port()
    {
        var s = FreeSettings();
        using var holder = new UdpClient(new IPEndPoint(IPAddress.Any, s.RegionPort));
        Assert.Equal($"Port {s.RegionPort} (Region UDP) is already in use by another program.", PortCheck.FindConflict(s));
    }

    [Fact]
    public void PortCheck_skips_ports_held_by_children_about_to_be_adopted()
    {
        var s = FreeSettings();
        var holder = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        holder.Start();
        try
        {
            Assert.Null(PortCheck.FindConflict(s, new HashSet<int> { s.RobustPublicPort }));
        }
        finally { holder.Stop(); }
    }

    [Fact]
    public void PortCheck_checks_mariadb_on_loopback()
    {
        var s = FreeSettings();
        var holder = new TcpListener(IPAddress.Loopback, s.MariaDbPort);
        holder.Start();
        try
        {
            Assert.Equal($"Port {s.MariaDbPort} (MariaDB) is already in use by another program.", PortCheck.FindConflict(s));
        }
        finally { holder.Stop(); }
    }
}
