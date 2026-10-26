using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Grid;

public sealed record OnlineAvatar(string Name, Guid RegionId);

/// <param name="Available">False when the database could not be asked; <see cref="Avatars"/> is then empty.</param>
public sealed record OnlineList(IReadOnlyList<OnlineAvatar> Avatars, bool Available)
{
    public static readonly OnlineList Unavailable = new([], false);
}

/// <summary>Who is in-world right now; the host can swap in a test double.</summary>
public interface IOnlineUsers
{
    /// <summary>Never throws for a database that is down or not set up yet: returns <see cref="OnlineList.Unavailable"/>.</summary>
    Task<OnlineList> ListAsync(CancellationToken ct);
}

/// <summary>
/// Reads Robust's presence data (an avatar in a region has a non-zero RegionID) as robustuser. The settings are read
/// per call, so a changed port or password is used at once. Each call is its own result, safe for concurrent requests.
/// Any failure but the caller's cancellation (a database that is down, an odd row) gives "unavailable", never an error page.
/// </summary>
public sealed class OnlineUsers(Func<LauncherSettings> settings, ILogger<OnlineUsers>? log = null) : IOnlineUsers
{
    private int _warned;

    // Short: the dashboard polls this and must never hang on a database that is down.
    private const uint TimeoutSeconds = 3;

    private const string Query =
        "SELECT u.FirstName, u.LastName, p.RegionID FROM Presence p JOIN UserAccounts u ON u.PrincipalID = p.UserID " +
        "WHERE p.RegionID <> '00000000-0000-0000-0000-000000000000'";

    public async Task<OnlineList> ListAsync(CancellationToken ct)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(Query, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<OnlineAvatar>();
            while (await reader.ReadAsync(ct))
            {
                // char(36) may come back as a Guid or as text, depending on the connector's GUID handling.
                var region = reader.GetValue(2) is Guid g ? g : Guid.TryParse(Convert.ToString(reader.GetValue(2)), out var p) ? p : Guid.Empty;
                list.Add(new OnlineAvatar($"{reader.GetString(0)} {reader.GetString(1)}", region));
            }
            // An avatar with two presence rows (a stale session) is listed once.
            return new OnlineList(list.DistinctBy(a => a.Name).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), true);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return OnlineList.Unavailable; // the database is down or still starting: expected, not logged
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Unexpected (a NULL name, a cast): warn once, then only at Debug, since the dashboard polls every few seconds.
            var level = Interlocked.Exchange(ref _warned, 1) == 0 ? LogLevel.Warning : LogLevel.Debug;
            log?.Log(level, "Who's online could not be read ({Error}: {Message}).", e.GetType().Name, e.Message);
            return OnlineList.Unavailable;
        }
    }
}
