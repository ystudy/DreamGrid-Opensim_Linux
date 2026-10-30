using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Plans;

/// <summary>The grid services the launcher asks about accounts, passwords and the map.</summary>
public enum GridServiceKind { Accounts, Authentication, Grid }

/// <summary>
/// Where Robust, the grid service, is. A Region Server that has joined a grid uses the Grid Asset Server's address and
/// ports; a Connected Server uses the other grid's addresses; every other install (and a Region Server or Connected
/// Server whose setup is not finished) uses its own Robust on 127.0.0.1.
/// </summary>
public static class GridEndpoints
{
    private static JoinedGrid? Joined(LauncherSettings s) => s.Plan == OwnerPlan.RegionServer ? s.Join : null;

    private static ConnectedGrid? Other(LauncherSettings s) => s.Plan == OwnerPlan.Connected ? s.Connected : null;

    /// <summary>The preset grid a Connected Server joined, or null for another grid and every other install.</summary>
    public static GridPreset? Preset(LauncherSettings s) => Other(s) is { } c ? GridPresets.ById(c.Preset) : null;

    /// <summary>The address of Robust's private services (accounts, authentication).</summary>
    public static Uri PrivateBase(LauncherSettings s) =>
        Other(s) is { } c ? new Uri(c.ServicesUri.TrimEnd('/'))
        : Joined(s) is { } j ? new Uri($"http://{j.Host}:{j.PrivatePort}") : new Uri($"http://127.0.0.1:{s.RobustPrivatePort}");

    /// <summary>
    /// The address of one private service: a preset grid keeps each at its own address, every other install keeps
    /// them all at <see cref="PrivateBase"/>.
    /// </summary>
    public static Uri ServiceBase(LauncherSettings s, GridServiceKind kind) =>
        Preset(s) is { } p
            ? new Uri(kind switch
            {
                GridServiceKind.Accounts => p.AccountsUri,
                GridServiceKind.Authentication => p.AuthenticationUri,
                _ => p.GridUri,
            })
            : PrivateBase(s);

    /// <summary>The address of Robust's public services (grid info, login).</summary>
    public static Uri PublicBase(LauncherSettings s) =>
        Other(s) is { } c ? new Uri(c.Address.TrimEnd('/'))
        : Joined(s) is { } j ? new Uri($"http://{j.Host}:{j.PublicPort}") : new Uri($"http://127.0.0.1:{s.RobustPublicPort}");

    /// <summary>The port of Robust's private services.</summary>
    public static int PrivatePort(LauncherSettings s) =>
        Other(s) is { } c ? new Uri(c.ServicesUri).Port : Joined(s)?.PrivatePort ?? s.RobustPrivatePort;

    /// <summary>
    /// What the region INI's <c>BaseURL</c> and <c>PublicPort</c> advertise as the grid's home: on a single-machine
    /// plan, this computer's address and Robust's public port. On a Region Server or Connected Server it is the grid's
    /// own published login address (Robust's <c>BaseURL:PublicPort/</c>, the get_grid_info <c>login</c>), split into
    /// scheme and host, and port, so the grid's own users are not taken for hypergrid visitors when the owner typed a
    /// LAN address. A login address that can't be read falls back to the typed address. Only the advertised address
    /// follows the login: this computer still reaches the grid's services at the typed address (<see cref="PublicBase"/>,
    /// <see cref="PrivateUrl"/>).
    /// </summary>
    public static (string BaseUrl, int PublicPort) Advertised(LauncherSettings s)
    {
        if (Other(s) is { } c)
            return Split(c.LoginUri) ?? Split(c.Address) ?? ($"http://{HostAddress.Effective(s.Hostname)}", s.RobustPublicPort);
        if (Joined(s) is not { } j) return ($"http://{HostAddress.Effective(s.Hostname)}", s.RobustPublicPort);
        return Split(j.LoginUri) ?? ($"http://{j.Host}", j.PublicPort);
    }

    private static (string BaseUrl, int PublicPort)? Split(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0
            ? ($"{uri.Scheme}://{uri.Host}", uri.Port)
            : null;

    /// <summary>
    /// The scheme and host written into <c>PrivURL</c>: the other grid's services on a Connected Server, the Grid Asset
    /// Server on a Region Server, otherwise this computer.
    /// </summary>
    public static string PrivateUrl(LauncherSettings s)
    {
        if (Other(s) is { } c)
        {
            var services = new Uri(c.ServicesUri);
            return $"{services.Scheme}://{services.Host}";
        }
        return $"http://{Joined(s)?.Host ?? "127.0.0.1"}";
    }

    /// <summary>
    /// The login address viewers use: the other grid's on a Connected Server, the joined grid's on a Region Server,
    /// otherwise <c>http://{host}:{port}</c>.
    /// </summary>
    public static string LoginUri(LauncherSettings s, string host) =>
        Other(s)?.LoginUri ?? Joined(s)?.LoginUri ?? $"http://{host}:{s.RobustPublicPort}";
}
