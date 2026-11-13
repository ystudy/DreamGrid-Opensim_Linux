using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Database;

/// <summary>Removes an estate nothing is in any more, from the regions' database.</summary>
public interface IEstateRemover
{
    /// <summary>
    /// Removes estate <paramref name="estateId"/> with its access list, managers, groups and bans when no region is linked to
    /// it, and returns true; false (nothing touched) while a region still is. One transaction. Throws
    /// <see cref="StaffDirectoryUnavailableException"/> when the database can't be asked.
    /// </summary>
    Task<bool> RemoveIfEmptyAsync(int estateId, CancellationToken ct);
}

/// <summary>
/// <see cref="IEstateRemover"/> on the <c>opensim</c> database as opensimuser, with OpenSim's own estate tables (its EstateStore
/// migrations). Used for the estate the launcher made for a resident who is deleted, once their regions are back in the grid's.
/// </summary>
public sealed class EstateRemover(Func<LauncherSettings> settings) : IEstateRemover
{
    private const uint TimeoutSeconds = 10;

    private const string Linked = "SELECT COUNT(*) FROM `opensim`.`estate_map` WHERE `EstateID` = @e FOR UPDATE";

    public static readonly IReadOnlyList<string> Statements =
    [
        "DELETE FROM `opensim`.`estate_users` WHERE `EstateID` = @e",
        "DELETE FROM `opensim`.`estate_managers` WHERE `EstateID` = @e",
        "DELETE FROM `opensim`.`estate_groups` WHERE `EstateID` = @e",
        "DELETE FROM `opensim`.`estateban` WHERE `EstateID` = @e",
        "DELETE FROM `opensim`.`estate_settings` WHERE `EstateID` = @e",
    ];

    public async Task<bool> RemoveIfEmptyAsync(int estateId, CancellationToken ct)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "opensim", "opensimuser", s.Db.OpensimPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var check = new MySqlCommand(Linked, conn, tx))
            {
                check.Parameters.AddWithValue("@e", estateId);
                if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0) return false;
            }
            foreach (var sql in Statements)
            {
                await using var cmd = new MySqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("@e", estateId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return true;
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            throw new StaffDirectoryUnavailableException("The estate could not be removed.", e);
        }
    }
}
