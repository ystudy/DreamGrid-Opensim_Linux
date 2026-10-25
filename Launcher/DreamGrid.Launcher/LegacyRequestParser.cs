using DreamGrid.Launcher.Core.Api;

namespace DreamGrid.Launcher;

/// <summary>Builds a <see cref="LegacyRequest"/> from the query string and (for a POST) the form; names are case-insensitive (API.txt).</summary>
public static class LegacyRequestParser
{
    public static LegacyRequest From(IQueryCollection query, IFormCollection? form)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in query) raw[key] = value.ToString();
        if (form is not null)
            foreach (var (key, value) in form) raw[key] = value.ToString(); // the form wins over the query

        string? Get(string name) => raw.TryGetValue(name, out var v) ? v : null;

        return new LegacyRequest(
            Get("command") ?? "",
            Get("avatar"),
            Get("password"),
            Get("RegionName"),
            Guid.TryParse(Get("RegionUUID"), out var uuid) ? uuid : null,
            raw);
    }
}
