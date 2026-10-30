using System.Xml;
using System.Xml.Linq;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Plans;

/// <summary>A grid a Connected Server can join by one choice: the addresses its own INI prototype points at.</summary>
public sealed record GridPreset(string Id, string Title, string Address, string AccountsUri, string AuthenticationUri, string GridUri, string ProtoFolder);

public static class GridPresets
{
    public static IReadOnlyList<GridPreset> All { get; } =
    [
        new("osgrid", "OSgrid", "http://login.osgrid.org/", "http://users.osgrid.org", "http://login.osgrid.org", "http://grid.osgrid.org", "OsGrid"),
    ];

    public static GridPreset? ById(string? id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>What the owner typed for the other grid's address, as a URI with a host and port.</summary>
public static class OtherGridAddress
{
    /// <summary>The login service's usual port.</summary>
    public const int LoginPort = 8002;
    /// <summary>The usual port of the grid's services for region servers.</summary>
    public const int ServicesPort = 8003;

    /// <summary>
    /// <c>scheme://host:port/</c> for <paramref name="typed"/> (a name or IPv4 address, optionally with <c>:port</c> and an
    /// <c>http://</c> or <c>https://</c> start), using <paramref name="defaultPort"/> when it names none, or null with an
    /// owner-readable <paramref name="error"/>.
    /// </summary>
    public static Uri? Parse(string? typed, int defaultPort, out string? error)
    {
        var parsed = JoinAddress.Split(typed, defaultPort,
            "Type the other grid's address, like http://login.osgrid.org or grid.example.org.",
            "Type just the other grid's address, like http://login.osgrid.org or grid.example.org.",
            "The port after the colon in the other grid's address must be a number from 1 to 65535.", out error);
        return parsed is { } p ? new Uri($"{p.Scheme}://{p.Host}:{p.Port}/") : null;
    }
}

/// <summary>What another grid published through get_grid_info.</summary>
public sealed record OtherGridInfo(string GridName, string LoginUri, string? GatekeeperUri, bool IsGridAssetServer);

public static class OtherGridInfoParser
{
    /// <summary>
    /// Reads the get_grid_info answer <paramref name="gridInfoXml"/> from <paramref name="address"/>. Throws
    /// <see cref="GridJoinException"/> when it is not grid information.
    /// </summary>
    public static OtherGridInfo Parse(Uri address, string gridInfoXml)
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
            throw new GridJoinException($"{address} answered, but it didn't answer like a grid. Check that it is the grid's login address, not its website.");

        string? Get(string key) => info.Element(key)?.Value.Trim() is { Length: > 0 } v ? v : null;

        return new OtherGridInfo(
            Get("gridname") ?? address.Host,
            Get("login") ?? address.ToString(),
            Get("gatekeeper"),
            Get(GridInfoKeys.Plan) == PlanCatalog.Get(OwnerPlan.GridAsset).Id);
    }
}

/// <summary>Asks another grid about itself and checks the address for region servers it gave.</summary>
public interface IOtherGridClient
{
    /// <summary>Reads the grid information at <paramref name="address"/>; throws <see cref="GridJoinException"/> with a message for the owner.</summary>
    Task<OtherGridInfo> ReadAsync(Uri address, CancellationToken ct);

    /// <summary>Checks that <paramref name="services"/> answers like a grid's services; throws <see cref="GridJoinException"/> with a message for the owner.</summary>
    Task CheckServicesAsync(Uri services, CancellationToken ct);
}

/// <summary>Reads <c>{address}get_grid_info</c> and asks <c>{services}grid</c> for a region range nothing occupies.</summary>
public sealed class OtherGridClient(HttpClient http) : IOtherGridClient
{
    public async Task<OtherGridInfo> ReadAsync(Uri address, CancellationToken ct)
    {
        string xml;
        try
        {
            using var response = await http.GetAsync(new Uri(address, "get_grid_info"), ct);
            response.EnsureSuccessStatusCode();
            xml = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new GridJoinException($"Nothing answered at {address}. Check the address, and this computer's internet connection.", noAnswer: true);
        }
        return OtherGridInfoParser.Parse(address, xml);
    }

    public async Task CheckServicesAsync(Uri services, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["METHOD"] = "get_region_range",
            ["SCOPEID"] = "00000000-0000-0000-0000-000000000000",
            ["XMIN"] = "256000",
            ["YMIN"] = "256000",
            ["XMAX"] = "256256",
            ["YMAX"] = "256256",
        };
        string xml;
        try
        {
            using var response = await http.PostAsync(new Uri(services, "grid"), new FormUrlEncodedContent(form), ct);
            xml = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new GridJoinException($"Nothing answered at {services}. Check the address for region servers the grid gave you.", noAnswer: true);
        }

        string? root;
        try
        {
            root = XDocument.Parse(xml).Root?.Name.LocalName;
        }
        catch (XmlException)
        {
            root = null;
        }
        if (root != "ServerResponse")
            throw new GridJoinException($"{services} answered, but not like a grid's services. Check the address for region servers the grid gave you.");
    }
}
