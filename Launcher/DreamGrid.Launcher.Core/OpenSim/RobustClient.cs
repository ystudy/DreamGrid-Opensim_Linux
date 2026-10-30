using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.OpenSim;

public sealed record RobustAccount(Guid PrincipalId, string FirstName, string LastName, int UserLevel, string Email);

public interface IRobustClient
{
    Task<bool> IsReadyAsync(CancellationToken ct);
    Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct);
    Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct);

    /// <summary>Every region on the grid's map (all computers), from the grid service. Empty when there are none.</summary>
    Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct);

    /// <summary>The regions on the grid's map inside <paramref name="area"/> only (a big grid's whole map is too much to read).</summary>
    Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(MapArea area, CancellationToken ct);
}

/// <summary>
/// Robust's public grid-info endpoint plus the private account, auth and grid services. Their addresses are read from
/// the settings on every call, so a changed Robust port applies at once.
/// </summary>
public sealed class RobustClient(HttpClient http, Func<LauncherSettings> settings) : IRobustClient
{
    /// <summary>
    /// Robust's get_region_range filters regions by their origin (south-west corner), and the largest region is 4096 m =
    /// 16 squares, so a region reaching into an area can start up to this many squares west or south of it.
    /// </summary>
    private const int LargestRegionSquares = 16;

    private Uri PublicBase => GridEndpoints.PublicBase(settings());

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(new Uri(PublicBase, "/get_grid_info"), ct);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return body.Contains("<gridinfo>", StringComparison.OrdinalIgnoreCase)
                || body.Contains("gridname", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    public async Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct)
    {
        var root = await PostAsync(GridServiceKind.Accounts, "/accounts", ct,
            ("METHOD", "getaccount"), ("FirstName", first), ("LastName", last));
        var result = root?.Element("result");
        if (result is null || !result.HasElements) return null; // "null" or absent

        string Field(string name) => result.Element(name)?.Value.Trim() ?? "";
        if (!Guid.TryParse(Field("PrincipalID"), out var id)) return null;
        _ = int.TryParse(Field("UserLevel"), out var level);
        return new RobustAccount(id, Field("FirstName"), Field("LastName"), level, Field("Email"));
    }

    public async Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct)
    {
        var md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(plainPassword)));
        var root = await PostAsync(GridServiceKind.Authentication, "/auth/plain", ct,
            ("METHOD", "authenticate"), ("PRINCIPAL", principal.ToString()), ("PASSWORD", md5));
        return !string.IsNullOrWhiteSpace(root?.Element("Token")?.Value);
    }

    public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct) =>
        GetRegionRangeAsync("0", "0", "16776960", "16776960", ct);

    public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(MapArea area, CancellationToken ct) =>
        GetRegionRangeAsync((Math.Max(0, area.XMin - LargestRegionSquares) * 256).ToString(),
            (Math.Max(0, area.YMin - LargestRegionSquares) * 256).ToString(),
            (area.XMax * 256 + 255).ToString(), (area.YMax * 256 + 255).ToString(), ct);

    private async Task<IReadOnlyList<MapRegion>> GetRegionRangeAsync(string xMin, string yMin, string xMax, string yMax, CancellationToken ct)
    {
        // The reply is <region0 type="List">…</region0><region1…>; "null" comes back as <result>null</result>.
        var root = await PostAsync(GridServiceKind.Grid, "/grid", ct,
            ("METHOD", "get_region_range"), ("SCOPEID", Guid.Empty.ToString()),
            ("XMIN", xMin), ("YMIN", yMin), ("XMAX", xMax), ("YMAX", yMax));
        var regions = new List<MapRegion>();
        if (root is null) return regions;
        foreach (var e in root.Elements().Where(e => e.Name.LocalName.StartsWith("region", StringComparison.Ordinal) && e.HasElements))
        {
            string Field(string name) => e.Element(name)?.Value.Trim() ?? "";
            if (!Guid.TryParse(Field("uuid"), out var id)) continue;
            if (!int.TryParse(Field("locX"), out var locX) || !int.TryParse(Field("locY"), out var locY)) continue;
            _ = int.TryParse(Field("sizeX"), out var sizeX);
            _ = int.TryParse(Field("sizeY"), out var sizeY);
            var size = Math.Max(1, Math.Max(sizeX, sizeY) / 256);
            regions.Add(new MapRegion(id, Field("regionName"), locX / 256, locY / 256, size));
        }
        return regions;
    }

    private async Task<XElement?> PostAsync(GridServiceKind service, string path, CancellationToken ct, params (string Key, string Value)[] fields)
    {
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        using var resp = await http.PostAsync(new Uri(GridEndpoints.ServiceBase(settings(), service), path), content, ct);
        resp.EnsureSuccessStatusCode();
        try { return XDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).Root; }
        catch (System.Xml.XmlException) { return null; }
    }
}
