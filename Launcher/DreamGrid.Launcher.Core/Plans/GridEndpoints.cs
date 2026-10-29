using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Plans;

/// <summary>
/// Where Robust, the grid service, is. A Region Server that has joined a grid uses the Grid Asset Server's address and
/// ports; every other install (and a Region Server whose setup is not finished) uses its own Robust on 127.0.0.1.
/// </summary>
public static class GridEndpoints
{
    private static JoinedGrid? Joined(LauncherSettings s) => s.Plan == OwnerPlan.RegionServer ? s.Join : null;

    /// <summary>The address of Robust's private services (accounts, authentication).</summary>
    public static Uri PrivateBase(LauncherSettings s) =>
        Joined(s) is { } j ? new Uri($"http://{j.Host}:{j.PrivatePort}") : new Uri($"http://127.0.0.1:{s.RobustPrivatePort}");

    /// <summary>The address of Robust's public services (grid info, login).</summary>
    public static Uri PublicBase(LauncherSettings s) =>
        Joined(s) is { } j ? new Uri($"http://{j.Host}:{j.PublicPort}") : new Uri($"http://127.0.0.1:{s.RobustPublicPort}");

    /// <summary>The port of Robust's public services.</summary>
    public static int PublicPort(LauncherSettings s) => Joined(s)?.PublicPort ?? s.RobustPublicPort;

    /// <summary>The port of Robust's private services.</summary>
    public static int PrivatePort(LauncherSettings s) => Joined(s)?.PrivatePort ?? s.RobustPrivatePort;

    /// <summary>The host written into <c>BaseURL</c>: the Grid Asset Server on a Region Server, otherwise this computer's address.</summary>
    public static string ServiceHost(LauncherSettings s) =>
        Joined(s)?.Host ?? HostAddress.Effective(s.Hostname);

    /// <summary>The host written into <c>PrivURL</c>: the Grid Asset Server on a Region Server, otherwise this computer.</summary>
    public static string PrivateHost(LauncherSettings s) => Joined(s)?.Host ?? "127.0.0.1";

    /// <summary>The login address viewers use: the joined grid's on a Region Server, otherwise <c>http://{host}:{port}</c>.</summary>
    public static string LoginUri(LauncherSettings s, string host) =>
        Joined(s)?.LoginUri ?? $"http://{host}:{s.RobustPublicPort}";
}
