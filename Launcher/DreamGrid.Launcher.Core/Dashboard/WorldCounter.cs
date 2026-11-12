using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Dashboard;

/// <summary>
/// The dashboard's numbers (design 2026-11-05-dashboard). Each is null when it couldn't be counted: a database that is down, a
/// table OpenSim hasn't made yet, or a plan whose accounts live on another grid. Residents-only numbers join UserAccounts, so
/// hypergrid visitors (who have GridUser and profile rows but no account here) aren't counted.
/// </summary>
public sealed record WorldCounts(
    long? Residents, long? JoinedThisMonth, long? NeverSignedIn, long? SignedInToday, long? SignedInThisMonth,
    long? LandSquareMetres, long? MapRegions,
    long? Objects, long? LinkedObjects, long? Parcels, long? ParcelsForSale, long? Estates,
    long? Groups, long? HypergridLinks, long? Profiles, long? Picks, long? Classifieds)
{
    public static readonly WorldCounts Unknown = new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
}

/// <summary>Counts the dashboard's numbers; the host can swap in a test double.</summary>
public interface IWorldCounter
{
    /// <param name="monthStart">The start of this month, local time.</param>
    /// <param name="dayStart">The start of today, local time.</param>
    Task<WorldCounts> CountAsync(DateTimeOffset monthStart, DateTimeOffset dayStart, CancellationToken ct);
}

/// <summary>
/// Read-only COUNT and SUM queries as robustuser (the robust database) and opensimuser (the regions' database). Every query is
/// fixed text with only the two times as parameters, has its own short timeout, and fails on its own.
/// </summary>
public sealed class WorldCounter(Func<LauncherSettings> settings, ILogger<WorldCounter>? log = null) : IWorldCounter
{
    private const uint TimeoutSeconds = 3;

    // OpenSim's own "GRID SERVICES" account (Constants.servicesAgentIDString), which Robust makes on its first start: no
    // person, so never a resident.
    // Not the library owner, and not a starter avatar's model (spec 2026-11-11-starter-avatars-design §6.5): sign-up refuses names
    // in that pattern, so only models have them.
    internal const string Person = "u.`PrincipalID` <> '6571e388-6218-4574-87db-f9379718315e' " +
        "AND NOT (u.`LastName` = 'Avatar' AND u.`FirstName` REGEXP '^Starter[0-9]{2}$')";

    // A hypergrid link is a regions row with the Hyperlink flag (512).
    private const string NotLink = "(`flags` & 512) = 0";

    public async Task<WorldCounts> CountAsync(DateTimeOffset monthStart, DateTimeOffset dayStart, CancellationToken ct)
    {
        var s = settings();
        var month = monthStart.ToUnixTimeSeconds();
        var day = dayStart.ToUnixTimeSeconds();
        // The robust database is this grid's own only where this computer runs the grid; elsewhere it is empty and unused.
        var robust = AdminRule.HasStaffPages(s);

        async Task<long?> R(string sql) => robust ? await ScalarAsync(s, "robust", "robustuser", s.Db.RobustPassword, sql, month, day, ct) : null;
        async Task<long?> O(string sql) => await ScalarAsync(s, "opensim", "opensimuser", s.Db.OpensimPassword, sql, month, day, ct);

        // The two databases are asked side by side; within each, one query after another, so a busy grid gets one connection
        // per database at a time.
        async Task<long?[]> Robust() => [
            await R($"SELECT COUNT(*) FROM `UserAccounts` u WHERE {Person}"),
            await R($"SELECT COUNT(*) FROM `UserAccounts` u WHERE u.`Created` >= @month AND {Person}"),
            await R("SELECT COUNT(*) FROM `UserAccounts` u LEFT JOIN `GridUser` g ON g.`UserID` = u.`PrincipalID` " +
                $"WHERE (g.`UserID` IS NULL OR g.`Login` = '0') AND {Person}"),
            // Login is Unix seconds as text, so it can't use an index: one scan for both numbers, joined to UserAccounts so
            // hypergrid visitors aren't counted.
            await R($"SELECT COALESCE(SUM(CAST(g.`Login` AS UNSIGNED) >= @day), 0) FROM `GridUser` g JOIN `UserAccounts` u ON u.`PrincipalID` = g.`UserID` WHERE {Person}"),
            await R($"SELECT COALESCE(SUM(CAST(g.`Login` AS UNSIGNED) >= @month), 0) FROM `GridUser` g JOIN `UserAccounts` u ON u.`PrincipalID` = g.`UserID` WHERE {Person}"),
            await R($"SELECT COALESCE(SUM(CAST(`sizeX` AS UNSIGNED) * CAST(`sizeY` AS UNSIGNED)), 0) FROM `regions` WHERE {NotLink}"),
            await R($"SELECT COUNT(*) FROM `regions` WHERE {NotLink}"),
            await R("SELECT COUNT(*) FROM `os_groups_groups`"),
            await R("SELECT COUNT(*) FROM `regions` WHERE (`flags` & 512) <> 0"),
            // OpenSim adds an empty profile row the first time anyone's profile is opened: count only ones with something in them.
            await R("SELECT COUNT(*) FROM `userprofile` p JOIN `UserAccounts` u ON u.`PrincipalID` = p.`useruuid` " +
                $"WHERE {Person} AND (COALESCE(p.`profileAboutText`, '') <> '' OR (p.`profileImage` IS NOT NULL AND p.`profileImage` <> '00000000-0000-0000-0000-000000000000'))"),
            await R("SELECT COUNT(*) FROM `userpicks`"),
            await R("SELECT COUNT(*) FROM `classifieds`"),
        ];
        async Task<long?[]> Regions() => [
            await O("SELECT COUNT(*) FROM `prims`"),
            await O("SELECT COUNT(DISTINCT `SceneGroupID`) FROM `prims`"),
            await O("SELECT COUNT(*) FROM `land`"),
            // ParcelFlags.ForSale = 1 << 14.
            await O("SELECT COUNT(*) FROM `land` WHERE (`LandFlags` & 16384) <> 0"),
            await O("SELECT COUNT(*) FROM `estate_settings`"),
        ];
        var both = await Task.WhenAll(Robust(), Regions());
        var r = both[0];
        var o = both[1];
        return new WorldCounts(
            Residents: r[0], JoinedThisMonth: r[1], NeverSignedIn: r[2], SignedInToday: r[3], SignedInThisMonth: r[4],
            LandSquareMetres: r[5], MapRegions: r[6],
            Objects: o[0], LinkedObjects: o[1], Parcels: o[2], ParcelsForSale: o[3], Estates: o[4],
            Groups: r[7], HypergridLinks: r[8], Profiles: r[9], Picks: r[10], Classifieds: r[11]);
    }

    private async Task<long?> ScalarAsync(LauncherSettings s, string database, string user, string password, string sql, long month, long day,
        CancellationToken ct)
    {
        try
        {
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, database, user, password, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = (int)TimeoutSeconds };
            cmd.Parameters.AddWithValue("@month", month);
            cmd.Parameters.AddWithValue("@day", day);
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? 0 : Convert.ToInt64(value);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            // A database that is down or a table not made yet: expected, shown as "—".
            log?.LogDebug("A dashboard count could not be read ({Error}: {Message}).", e.GetType().Name, e.Message);
            return null;
        }
    }
}
