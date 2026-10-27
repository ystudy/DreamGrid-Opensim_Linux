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
        var free = FreeSettings();
        var s = free with
        {
            Programs = [new ProgramSettings("Welcome", free.RegionPort, free.RegionConsolePort)],
            Regions = [new RegionSettings("Welcome", "Welcome", Guid.NewGuid(), UdpPort: free.RegionPort)],
        };
        using var holder = new UdpClient(new IPEndPoint(IPAddress.Any, s.RegionPort));
        Assert.Equal($"Port {s.RegionPort} (Region Welcome UDP) is already in use by another program.", PortCheck.FindConflict(s));
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

    [Theory]
    [InlineData("Robust public")]
    [InlineData("Robust console")]
    [InlineData("Web")]
    public void PortCheck_reports_a_port_held_on_loopback_only(string name)
    {
        // On Windows a 0.0.0.0 bind succeeds next to another program's 127.0.0.1 listener, which then wins.
        var s = FreeSettings();
        var port = name switch { "Robust public" => s.RobustPublicPort, "Robust console" => s.RobustConsolePort, _ => s.WebPort };
        var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Start();
        try
        {
            Assert.Equal($"Port {port} ({name}) is already in use by another program.", PortCheck.FindConflict(s));
        }
        finally { holder.Stop(); }
    }

    [Fact]
    public void Port_check_names_the_region_of_a_taken_program_port()
    {
        var free = FreeSettings();
        var http = FreePort();
        var s = free with
        {
            Programs = [new ProgramSettings("Home", http, FreePort())],
            Regions = [new RegionSettings("Home", "Beach", Guid.NewGuid(), UdpPort: http)],
        };
        var holder = new TcpListener(IPAddress.Any, http);
        holder.Start();
        try
        {
            var conflict = PortCheck.FindConflictPort(s, new HashSet<int>(), new HashSet<string> { "Home" });

            Assert.Equal(http, conflict?.Port);
            Assert.Contains("(Region Beach)", conflict?.Message);
            Assert.Null(PortCheck.FindConflictPort(s, new HashSet<int>(), new HashSet<string>()));
        }
        finally { holder.Stop(); }
    }
}
