using System.Text.Json;

namespace DreamGrid.Launcher.Core.OpenSim;

/// <summary>
/// An alert POSTed by the RegionReady module: "region_ready" (Login enabled/disabled) or
/// "regionpid" (carries the region process id).
/// </summary>
public sealed record RegionAlert(string Alert, string Login, string RegionName, Guid RegionId, int? Pid)
{
    /// <summary>Parses an alert payload; null when it is not a recognisable alert.</summary>
    public static RegionAlert? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var alert = Str(root, "alert");
            if (string.IsNullOrEmpty(alert)) return null;

            _ = Guid.TryParse(Str(root, "region_id"), out var regionId);
            return new RegionAlert(alert, Str(root, "login"), Str(root, "region_name"), regionId, Int(root, "pid"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Int(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
        return null;
    }
}
