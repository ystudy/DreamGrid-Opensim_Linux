using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Database;

/// <summary>Deletes an avatar from Robust's database (OpenSim's console has no <c>delete user</c>).</summary>
public interface IAccountRemover
{
    /// <summary>
    /// Deletes every row of <paramref name="principal"/> in one transaction: nothing is left half-deleted. Throws
    /// <see cref="StaffDirectoryUnavailableException"/> when the database can't be asked.
    /// </summary>
    Task DeleteAsync(Guid principal, CancellationToken ct);
}

/// <summary>
/// <see cref="IAccountRemover"/> on Robust's database as robustuser. Meant for avatars that never logged in (sign-ups nobody
/// confirmed, or one whose making failed half-way): their inventory is only the default folders and items <c>create user</c>
/// made, so no assets are touched.
/// </summary>
public sealed class AccountRemover(Func<LauncherSettings> settings) : IAccountRemover
{
    private const uint TimeoutSeconds = 10;

    /// <summary>The statements, in order; each takes the principal as <c>@id</c> (OpenSim's lower-case <c>D</c> form).</summary>
    public static readonly IReadOnlyList<string> Statements =
    [
        "DELETE FROM `robust`.`inventoryitems` WHERE `avatarID` = @id",
        "DELETE FROM `robust`.`inventoryfolders` WHERE `agentID` = @id",
        "DELETE FROM `robust`.`Avatars` WHERE `PrincipalID` = @id",
        "DELETE FROM `robust`.`GridUser` WHERE `UserID` = @id",
        "DELETE FROM `robust`.`Presence` WHERE `UserID` = @id",
        "DELETE FROM `robust`.`tokens` WHERE `UUID` = @id",
        "DELETE FROM `robust`.`auth` WHERE `UUID` = @id",
        "DELETE FROM `robust`.`UserAccounts` WHERE `PrincipalID` = @id",
    ];

    public async Task DeleteAsync(Guid principal, CancellationToken ct)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var sql in Statements)
            {
                await using var cmd = new MySqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("@id", principal.ToString("D"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            throw new StaffDirectoryUnavailableException("The avatar could not be deleted.", e);
        }
    }
}
