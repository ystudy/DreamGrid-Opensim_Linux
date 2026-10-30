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

    /// <summary>The port of Robust's private services.</summary>
    public static int PrivatePort(LauncherSettings s) => Joined(s)?.PrivatePort ?? s.RobustPrivatePort;

    /// <summary>
    /// What the region INI's <c>BaseURL</c> and <c>PublicPort</c> advertise as the grid's home: everywhere but a joined
    /// Region Server, this computer's address and Robust's public port. On a Region Server it is the grid's own published
    /// login address (Robust's <c>BaseURL:PublicPort/</c>, the get_grid_info <c>login</c>), split into scheme and host,
    /// and port, so the grid's own users are not taken for hypergrid visitors when the owner typed a LAN address. A login
    /// address that can't be read falls back to the typed host and port. Only the advertised address follows the
    /// login: the Region Server itself still reaches the Grid Asset Server at the typed address (<see cref="PublicBase"/>,
    /// <see cref="PrivateHost"/>).
    /// </summary>
    public static (string BaseUrl, int PublicPort) Advertised(LauncherSettings s)
    {
        if (Joined(s) is not { } j) return ($"http://{HostAddress.Effective(s.Hostname)}", s.RobustPublicPort);
        if (Uri.TryCreate(j.LoginUri, UriKind.Absolute, out var login)
            && (login.Scheme == Uri.UriSchemeHttp || login.Scheme == Uri.UriSchemeHttps)
            && login.Host.Length > 0)
            return ($"{login.Scheme}://{login.Host}", login.Port);
        return ($"http://{j.Host}", j.PublicPort);
    }

    /// <summary>The host written into <c>PrivURL</c>: the Grid Asset Server on a Region Server, otherwise this computer.</summary>
    public static string PrivateHost(LauncherSettings s) => Joined(s)?.Host ?? "127.0.0.1";

    /// <summary>The login address viewers use: the joined grid's on a Region Server, otherwise <c>http://{host}:{port}</c>.</summary>
    public static string LoginUri(LauncherSettings s, string host) =>
        Joined(s)?.LoginUri ?? $"http://{host}:{s.RobustPublicPort}";
}
