using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Database;

/// <summary>Deletes an avatar from Robust's database (OpenSim's console has no <c>delete user</c>).</summary>
public interface IAccountRemover
{
    /// <summary>
    /// Deletes every row of <paramref name="principal"/> in one transaction: nothing is left half-deleted. Throws
    /// <see cref="StaffDirectoryUnavailableException"/> when the database can't be asked.
    /// </summary>
    Task DeleteAsync(Guid principal, CancellationToken ct);

    /// <summary>
    /// Deletes the account and everything in Robust's database tied to it (<see cref="AccountRemover.EverythingStatements"/>)
    /// in one transaction, and returns the tables it removed rows from. Throws <see cref="StaffDirectoryUnavailableException"/>
    /// when the database can't be asked; then nothing is deleted.
    /// </summary>
    Task<IReadOnlyList<string>> DeleteEverythingAsync(Guid principal, CancellationToken ct);
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

    /// <summary>
    /// For a resident deleted from the People page, in order, with the table each statement clears; each takes <c>@id</c>. The
    /// columns are OpenSim's own (its MySQL migrations): inventory, both sides of a friendship, group memberships, roles,
    /// invitations and the active group, the profile, picks, classifieds and notes (theirs, and notes others wrote about them),
    /// offline messages to them, their mute list, viewer preferences, hypergrid travel records, appearance, grid position,
    /// presence, sign-in tokens, the password, and last the account. Assets stay: others may hold copies. A table a grid never
    /// made (its service isn't loaded) has nothing to remove and is passed over.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Sql)> EverythingStatements =
    [
        ("inventoryitems", "DELETE FROM `robust`.`inventoryitems` WHERE `avatarID` = @id"),
        ("inventoryfolders", "DELETE FROM `robust`.`inventoryfolders` WHERE `agentID` = @id"),
        ("Friends", "DELETE FROM `robust`.`Friends` WHERE `PrincipalID` = @id OR `Friend` = @id"),
        ("os_groups_membership", "DELETE FROM `robust`.`os_groups_membership` WHERE `PrincipalID` = @id"),
        ("os_groups_rolemembership", "DELETE FROM `robust`.`os_groups_rolemembership` WHERE `PrincipalID` = @id"),
        ("os_groups_invites", "DELETE FROM `robust`.`os_groups_invites` WHERE `PrincipalID` = @id"),
        ("os_groups_principals", "DELETE FROM `robust`.`os_groups_principals` WHERE `PrincipalID` = @id"),
        ("userprofile", "DELETE FROM `robust`.`userprofile` WHERE `useruuid` = @id"),
        ("userpicks", "DELETE FROM `robust`.`userpicks` WHERE `creatoruuid` = @id"),
        ("classifieds", "DELETE FROM `robust`.`classifieds` WHERE `creatoruuid` = @id"),
        ("usernotes", "DELETE FROM `robust`.`usernotes` WHERE `useruuid` = @id OR `targetuuid` = @id"),
        ("usersettings", "DELETE FROM `robust`.`usersettings` WHERE `useruuid` = @id"),
        ("userdata", "DELETE FROM `robust`.`userdata` WHERE `UserId` = @id"),
        ("im_offline", "DELETE FROM `robust`.`im_offline` WHERE `PrincipalID` = @id"),
        ("MuteList", "DELETE FROM `robust`.`MuteList` WHERE `AgentID` = @id"),
        ("AgentPrefs", "DELETE FROM `robust`.`AgentPrefs` WHERE `PrincipalID` = @id"),
        ("hg_traveling_data", "DELETE FROM `robust`.`hg_traveling_data` WHERE `UserID` = @id"),
        ("Avatars", "DELETE FROM `robust`.`Avatars` WHERE `PrincipalID` = @id"),
        ("GridUser", "DELETE FROM `robust`.`GridUser` WHERE `UserID` = @id"),
        ("Presence", "DELETE FROM `robust`.`Presence` WHERE `UserID` = @id"),
        ("tokens", "DELETE FROM `robust`.`tokens` WHERE `UUID` = @id"),
        ("auth", "DELETE FROM `robust`.`auth` WHERE `UUID` = @id"),
        ("UserAccounts", "DELETE FROM `robust`.`UserAccounts` WHERE `PrincipalID` = @id"),
    ];

    private const string Tables = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'robust'";

    public async Task<IReadOnlyList<string>> DeleteEverythingAsync(Guid principal, CancellationToken ct)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var list = new MySqlCommand(Tables, conn))
            await using (var reader = await list.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) present.Add(reader.GetString(0));
            await using var tx = await conn.BeginTransactionAsync(ct);
            var removed = new List<string>();
            foreach (var (table, sql) in EverythingStatements)
            {
                if (!present.Contains(table)) continue;
                await using var cmd = new MySqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("@id", principal.ToString("D"));
                if (await cmd.ExecuteNonQueryAsync(ct) > 0) removed.Add(table);
            }
            await tx.CommitAsync(ct);
            return removed;
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            throw new StaffDirectoryUnavailableException("The avatar could not be deleted.", e);
        }
    }

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
