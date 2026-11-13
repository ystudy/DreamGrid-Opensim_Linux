using System.Runtime.InteropServices;

namespace GridGate.Launcher.Core.Settings;

/// <summary>
/// The operating system and processor this computer has, as far as the physics engines care. Pages and the grid
/// controller take it so a test can pretend to be another computer.
/// </summary>
public sealed record PlatformSupport(OSPlatform Os, Architecture Arch)
{
    /// <summary>The computer this process runs on.</summary>
    public static PlatformSupport Current { get; } = new(RegionSettingsRules.CurrentPlatform(), RuntimeInformation.OSArchitecture);

    /// <summary>Whether a Rapier library ships for this computer.</summary>
    public bool Rapier => RegionSettingsRules.RapierSupported(Os, Arch);

    /// <summary>The engines this computer can run.</summary>
    public IReadOnlyList<string> Engines => Rapier ? RegionSettingsRules.PhysicsEngines : [.. RegionSettingsRules.PhysicsEngines.Where(e => e != "Rapier")];

    /// <summary>
    /// Why <paramref name="engine"/> cannot run here, or null when it can. Use it for a stored value, to tell the user
    /// why a program will not start.
    /// </summary>
    public string? Unavailable(string? engine) => engine == "Rapier" && !Rapier ? RegionSettingsRules.RapierUnavailable : null;
}
