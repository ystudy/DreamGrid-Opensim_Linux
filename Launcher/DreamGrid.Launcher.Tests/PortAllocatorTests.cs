using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class PortAllocatorTests
{
    /// <summary>Version 1 defaults (web 8001, Robust 8002/8003/8010, MariaDB 3307), one program 8004/8011, a deleted region on 8005.</summary>
    private static LauncherSettings Defaults() => TestSettings.Create() with
    {
        Programs = [new ProgramSettings("Home", 8004, 8011)],
        Regions =
        [
            new RegionSettings("Home", "Home", Guid.NewGuid(), UdpPort: 8004),
            new RegionSettings("Home", "Gone", Guid.NewGuid(), 1001, 1000, UdpPort: 8005, DeletedAt: DateTimeOffset.UnixEpoch),
        ],
    };

    [Fact]
    public void Next_port_skips_configured_and_reserved_ports()
    {
        var s = Defaults();

        Assert.Equal(8006, PortAllocator.Next(s, 8004));
        Assert.Equal(8012, PortAllocator.Next(s, 8010));
        Assert.Equal(8007, PortAllocator.Next(s, 8004, new HashSet<int> { 8006 }));
    }

    [Fact]
    public void First_program_on_a_fresh_install_gets_version_1_ports()
    {
        var s = TestSettings.Create();

        Assert.Equal(8004, PortAllocator.Next(s, s.RegionPort));
        Assert.Equal(8011, PortAllocator.Next(s, s.RegionConsolePort, new HashSet<int> { 8004 }));
    }

    [Fact]
    public void Taken_lists_configured_and_assigned_ports()
    {
        Assert.Equal(new HashSet<int> { 8001, 3307, 8002, 8003, 8010, 8011, 8004, 8005 }, PortAllocator.Taken(Defaults()));
    }

    [Fact]
    public void Next_port_throws_past_65535()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PortAllocator.Next(Defaults(), 65535, new HashSet<int> { 65535 }));
        Assert.Equal("No free port from 65535.", ex.Message);
    }

    [Fact]
    public void Replace_moves_program_and_region_ports()
    {
        var moved = PortAllocator.Replace(Defaults(), 8004, 8104)!;

        Assert.Equal(8104, moved.Programs[0].HttpPort);
        Assert.Equal(8104, moved.Regions[0].UdpPort);
        Assert.Equal(8011, moved.Programs[0].ConsolePort);

        Assert.Equal(8111, PortAllocator.Replace(Defaults(), 8011, 8111)!.Programs[0].ConsolePort);
    }

    [Fact]
    public void Replace_returns_null_for_unassigned_port()
    {
        Assert.Null(PortAllocator.Replace(Defaults(), 8002, 8102));
    }
}
