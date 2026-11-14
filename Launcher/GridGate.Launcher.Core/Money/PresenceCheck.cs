using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>What Robust's Presence table says about a session.</summary>
public enum PresenceOutcome { Ok, NoSession, WrongRegion, Unavailable }

/// <summary>
/// Checks an avatar's session against Robust's <c>Presence</c> table (money design §7.2). Residents and hypergrid visitors
/// both have a row: Robust's login writes one, and so does the Gatekeeper for a visitor (GatekeeperService.LoginAgent).
/// </summary>
public sealed class PresenceCheck(Func<CancellationToken, Task<MySqlConnection>> openRobust)
{
    /// <summary>
    /// Ok when <paramref name="agentId"/> is logged in with both session ids and is now in one of <paramref name="regions"/>.
    /// </summary>
    public async Task<PresenceOutcome> CheckAsync(Guid agentId, Guid sessionId, Guid secureSessionId, IReadOnlySet<Guid> regions, CancellationToken ct)
    {
        if (agentId == Guid.Empty || sessionId == Guid.Empty || secureSessionId == Guid.Empty)
            return PresenceOutcome.NoSession;
        try
        {
            await using var conn = await openRobust(ct);
            await using var cmd = new MySqlCommand(
                "SELECT RegionID FROM Presence WHERE UserID = @u AND SessionID = @s AND SecureSessionID = @ss", conn);
            cmd.Parameters.AddWithValue("@u", agentId.ToString());
            cmd.Parameters.AddWithValue("@s", sessionId.ToString());
            cmd.Parameters.AddWithValue("@ss", secureSessionId.ToString());
            return await cmd.ExecuteScalarAsync(ct) switch
            {
                Guid region => regions.Contains(region) ? PresenceOutcome.Ok : PresenceOutcome.WrongRegion,
                string text when Guid.TryParse(text, out var region) => regions.Contains(region) ? PresenceOutcome.Ok : PresenceOutcome.WrongRegion,
                _ => PresenceOutcome.NoSession,
            };
        }
        catch (MySqlException)
        {
            return PresenceOutcome.Unavailable;
        }
    }

    /// <summary>Whether <paramref name="id"/> is a local account in Robust's <c>UserAccounts</c>. A database failure throws.</summary>
    public async Task<bool> IsLocalUserAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await openRobust(ct);
        await using var cmd = new MySqlCommand("SELECT 1 FROM UserAccounts WHERE PrincipalID = @id", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
