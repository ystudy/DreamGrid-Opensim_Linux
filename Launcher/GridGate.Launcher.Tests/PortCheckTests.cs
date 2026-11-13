using System.Net;
using System.Net.Sockets;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

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

    /// <summary>
    /// Settings whose ports were all free when chosen (distinct ephemeral ports). They are let go at once, so a test
    /// running at the same time may take one before the check: tests that expect one conflict check only the port they
    /// took (<see cref="AllBut"/>).
    /// </summary>
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

    /// <summary>Every port <paramref name="s"/> uses except <paramref name="taken"/>: the skip set that checks only the port a test took.</summary>
    private static HashSet<int> AllBut(LauncherSettings s, int taken)
    {
        var ports = new HashSet<int>
        {
            s.WebPort, s.RobustPublicPort, s.RobustPrivatePort, s.RegionPort, s.MariaDbPort, s.RobustConsolePort, s.RegionConsolePort,
        };
        foreach (var p in s.Programs) { ports.Add(p.HttpPort); ports.Add(p.ConsolePort); }
        foreach (var r in s.Regions) ports.Add(r.UdpPort);
        ports.Remove(taken);
        return ports;
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
                PortCheck.FindConflict(s, AllBut(s, s.RobustPublicPort)));
        }
        finally { holder.Stop(); }
    }

    [Fact]
    public void PortCheck_is_null_when_every_port_is_free()
    {
        // A port another test takes between FreeSettings and the check is reported, rightly: fresh ports then. The same
        // stranger never holds a fresh set three times running; a real fault fails every time.
        string? conflict = null;
        for (var attempt = 0; attempt < 3; attempt++)
            if ((conflict = PortCheck.FindConflict(FreeSettings())) is null) break;
        Assert.Null(conflict);
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
        Assert.Equal($"Port {s.RegionPort} (Region Welcome UDP) is already in use by another program.",
            PortCheck.FindConflict(s, AllBut(s, s.RegionPort)));
    }

    [Fact]
    public void PortCheck_skips_ports_held_by_children_about_to_be_adopted()
    {
        // Every other port is checked too, so a stranger taking one of them means fresh ports (as above).
        string? conflict = null;
        for (var attempt = 0; attempt < 3 && (attempt == 0 || conflict is not null); attempt++)
        {
            var s = FreeSettings();
            var holder = new TcpListener(IPAddress.Any, s.RobustPublicPort);
            holder.Start();
            try { conflict = PortCheck.FindConflict(s, new HashSet<int> { s.RobustPublicPort }); }
            finally { holder.Stop(); }
        }
        Assert.Null(conflict);
    }

    [Fact]
    public void PortCheck_checks_mariadb_on_loopback()
    {
        var s = FreeSettings();
        var holder = new TcpListener(IPAddress.Loopback, s.MariaDbPort);
        holder.Start();
        try
        {
            Assert.Equal($"Port {s.MariaDbPort} (MariaDB) is already in use by another program.",
                PortCheck.FindConflict(s, AllBut(s, s.MariaDbPort)));
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
            Assert.Equal($"Port {port} ({name}) is already in use by another program.", PortCheck.FindConflict(s, AllBut(s, port)));
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
            var others = AllBut(s, http);
            var conflict = PortCheck.FindConflictPort(s, others, new HashSet<string> { "Home" });

            Assert.Equal(http, conflict?.Port);
            Assert.Contains("(Region Beach)", conflict?.Message);
            Assert.Null(PortCheck.FindConflictPort(s, others, new HashSet<string>()));
        }
        finally { holder.Stop(); }
    }
}
