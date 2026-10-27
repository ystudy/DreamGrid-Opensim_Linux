using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Tests;

/// <summary>Settings with fixed secrets and version 1's default ports, for tests that need a whole <see cref="LauncherSettings"/>.</summary>
internal static class TestSettings
{
    public static LauncherSettings Create() => new()
    {
        Db = new DatabaseSecrets("root-pw", "robust-pw", "opensim-pw"),
        Console = new ConsoleSecrets("console", "console-pw"),
        MachineId = "machine-id",
    };

    /// <summary>Version 1's single program for <paramref name="r"/>: its HTTP and UDP port 8004, console 8011.</summary>
    public static List<ProgramSettings> ProgramFor(RegionSettings r, string physics = "ubODE") =>
        [new ProgramSettings(r.Instance, 8004, 8011, physics)];

    /// <summary><paramref name="r"/> alone, on UDP port 8004 when it has none.</summary>
    public static List<RegionSettings> RegionsFor(RegionSettings r) => [r.UdpPort == 0 ? r with { UdpPort = 8004 } : r];
}
