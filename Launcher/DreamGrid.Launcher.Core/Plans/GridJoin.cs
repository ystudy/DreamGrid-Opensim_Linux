using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Plans;

/// <summary>An owner-readable reason a Grid Asset Server could not be joined; the message is shown as is.</summary>
public sealed class GridJoinException(string message) : Exception(message);

/// <summary>The grid information keys a Grid Asset Server publishes through Robust's get_grid_info.</summary>
public static class GridInfoKeys
{
    public const string Version = "dreamgrid_version";
    public const string PrivatePort = "dreamgrid_private_port";
    public const string Hypergrid = "dreamgrid_hypergrid";
    public const string RegionPorts = "dreamgrid_region_ports";
    public const string Plan = "dreamgrid_plan";
}

/// <summary>What the owner typed for the Grid Asset Server's address, as a host and the grid service's public port.</summary>
public static class JoinAddress
{
    /// <summary>The grid service's public port unless the address names another.</summary>
    public const int DefaultPort = 8002;

    private const string NoAddress = "Type the Grid Asset Server's address, like 192.168.1.5 or grid.example.org.";

    /// <summary>
    /// The host and port in <paramref name="typed"/> (a name or IPv4 address, optionally with <c>:port</c> or an
    /// <c>http://</c> start and a closing slash), or null with an owner-readable <paramref name="error"/>.
    /// </summary>
    public static (string Host, int Port)? Parse(string? typed, out string? error)
    {
        error = null;
        var text = (typed ?? "").Trim();
        foreach (var scheme in new[] { "http://", "https://" })
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { text = text[scheme.Length..]; break; }
        if (text.Contains("://"))
        {
            error = "Type just the address, like 192.168.1.5 or grid.example.org.";
            return null;
        }
        text = text.TrimEnd('/');

        var host = text;
        var port = DefaultPort;
        var colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                error = "The port after the colon must be a number from 1 to 65535. The grid service's port is usually 8002.";
                return null;
            }
        }
        if (host.Length == 0)
        {
            error = NoAddress;
            return null;
        }
        if (SettingsValidator.HostnameError(host) is { } hostError)
        {
            error = hostError;
            return null;
        }
        return (host, port);
    }
}

/// <summary>Reads a Grid Asset Server's grid information into the join settings.</summary>
public static class GridJoin
{
    /// <summary>
    /// The join settings for the grid service at <paramref name="host"/>:<paramref name="publicPort"/> whose get_grid_info
    /// answer is <paramref name="gridInfoXml"/>. Throws <see cref="GridJoinException"/> when the answer is not grid
    /// information, is not from a DreamGrid install, or is from a plan other than a Grid Asset Server.
    /// </summary>
    public static JoinedGrid Parse(string host, int publicPort, string gridInfoXml)
    {
        XElement? info;
        try
        {
            info = XDocument.Parse(gridInfoXml).Root;
        }
        catch (XmlException)
        {
            info = null;
        }
        if (info is null || info.Name.LocalName != "gridinfo")
            throw new GridJoinException($"{host}:{publicPort} answered, but it didn't answer like a grid. Check the address and the port.");

        string? Get(string key) => info.Element(key)?.Value.Trim();

        var grid = PlanCatalog.Get(OwnerPlan.GridAsset);
        var plan = Get(GridInfoKeys.Plan);
        string[] required = [GridInfoKeys.Version, GridInfoKeys.PrivatePort, GridInfoKeys.Hypergrid];
        if (plan is null || required.Any(k => Get(k) is null))
            throw new GridJoinException($"The grid at {host}:{publicPort} isn't a DreamGrid Grid Asset Server. A Region Server can only join a {grid.Title}.");

        if (plan != grid.Id)
        {
            var other = PlanCatalog.ById(plan)?.Title;
            throw new GridJoinException(other is null
                ? $"The grid at {host}:{publicPort} isn't a DreamGrid Grid Asset Server. A Region Server can only join a {grid.Title}."
                : $"The grid at {host}:{publicPort} runs as a {other}. A Region Server can only join a {grid.Title}.");
        }

        var unreadable = new GridJoinException(
            $"The grid at {host}:{publicPort} gave join details this launcher can't read. Both computers should run the same DreamGrid version.");
        if (!int.TryParse(Get(GridInfoKeys.PrivatePort), NumberStyles.None, CultureInfo.InvariantCulture, out var privatePort)
            || !bool.TryParse(Get(GridInfoKeys.Hypergrid), out var hypergrid))
            throw unreadable;

        int? from = null, to = null;
        var ports = Get(GridInfoKeys.RegionPorts);
        if (!string.IsNullOrEmpty(ports))
        {
            var parts = ports.Split('-');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                throw unreadable;
            (from, to) = (a, b);
        }

        var gridName = Get("gridname") ?? host;
        var login = Get("login") ?? $"http://{host}:{publicPort}/";
        return new JoinedGrid(host, publicPort, privatePort, gridName, login, Get(GridInfoKeys.Version)!, hypergrid, from, to);
    }
}

/// <summary>Asks a Grid Asset Server how a Region Server joins it.</summary>
public interface IGridJoinClient
{
    /// <summary>Reads the join information at <paramref name="host"/>:<paramref name="port"/>; throws <see cref="GridJoinException"/> with a message for the owner.</summary>
    Task<JoinedGrid> ReadAsync(string host, int port, CancellationToken ct);
}

/// <summary>Reads <c>http://host:port/get_grid_info</c>.</summary>
public sealed class GridJoinClient(HttpClient http) : IGridJoinClient
{
    public async Task<JoinedGrid> ReadAsync(string host, int port, CancellationToken ct)
    {
        string xml;
        try
        {
            using var response = await http.GetAsync($"http://{host}:{port}/get_grid_info", ct);
            response.EnsureSuccessStatusCode();
            xml = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new GridJoinException($"Nothing answered at {host}:{port}. Check the address, and that the Grid Asset Server is running.");
        }
        return GridJoin.Parse(host, port, xml);
    }
}
