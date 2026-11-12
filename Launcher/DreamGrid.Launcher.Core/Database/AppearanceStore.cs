using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Database;

/// <summary>
/// One worn item in a stored appearance: <see cref="Slot"/> is OpenSim's own key, <c>Wearable i:j</c> for the j-th wearable of
/// type i, or <c>_ap_N</c> for an attachment on point N (a point can hold several).
/// </summary>
public sealed record WornItem(string Slot, Guid ItemId);

/// <summary>What an account is wearing, as Robust stored it (spec 2026-11-11-starter-avatars-design §6.3).</summary>
public interface IAppearanceStore
{
    /// <summary>
    /// The worn items, or null when the account has no stored appearance at all. Throws
    /// <see cref="StaffDirectoryUnavailableException"/> when the database can't be asked.
    /// </summary>
    Task<IReadOnlyList<WornItem>?> WornAsync(Guid principal, CancellationToken ct);
}

/// <summary><see cref="IAppearanceStore"/> on <c>robust.Avatars</c> (PrincipalID, Name, Value) as robustuser.</summary>
public sealed class AppearanceStore(Func<LauncherSettings> settings) : IAppearanceStore
{
    private const uint TimeoutSeconds = 10;

    public async Task<IReadOnlyList<WornItem>?> WornAsync(Guid principal, CancellationToken ct)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand("SELECT `Name`, `Value` FROM robust.Avatars WHERE `PrincipalID` = @id", conn);
            cmd.Parameters.AddWithValue("@id", principal.ToString());
            var rows = new List<(string, string)>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            return rows.Count == 0 ? null : Parse(rows);
        }
        catch (Exception e) when (e is MySqlException or InvalidCastException or FormatException or TimeoutException
                                      or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            throw new StaffDirectoryUnavailableException("The stored appearance could not be read.", e);
        }
    }

    /// <summary>
    /// The worn items in an appearance's rows (OpenSim's <c>AvatarData</c>): <c>Wearable i:j</c> = <c>item:asset</c>, <c>_ap_N</c> =
    /// comma-separated item ids. Empty item ids and rows that aren't worn items are skipped. Order: wearables, then attachments.
    /// </summary>
    internal static IReadOnlyList<WornItem> Parse(IEnumerable<(string Name, string Value)> rows)
    {
        var wearables = new List<WornItem>();
        var attachments = new List<WornItem>();
        foreach (var (name, value) in rows)
        {
            if (name.StartsWith("Wearable ", StringComparison.Ordinal))
            {
                var item = value.Split(':')[0];
                if (Guid.TryParse(item, out var id) && id != Guid.Empty) wearables.Add(new WornItem(name, id));
            }
            else if (name.StartsWith("_ap_", StringComparison.Ordinal))
            {
                foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Guid.TryParse(item, out var id) && id != Guid.Empty) attachments.Add(new WornItem(name, id));
            }
        }
        return [.. wearables, .. attachments];
    }
}
