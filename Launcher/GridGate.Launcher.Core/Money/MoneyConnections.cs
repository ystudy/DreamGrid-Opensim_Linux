using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>How the money code opens its databases, with the settings read at each call (ports and passwords may change).</summary>
public static class MoneyConnections
{
    private const uint TimeoutSeconds = 10;

    /// <summary>The money database as moneyuser.</summary>
    public static Func<CancellationToken, Task<MySqlConnection>> Money(Func<LauncherSettings> settings) =>
        ct => GridDb.OpenMoneyAsync(settings(), TimeoutSeconds, ct);

    /// <summary>The robust database as robustuser (Presence and UserAccounts).</summary>
    public static Func<CancellationToken, Task<MySqlConnection>> Robust(Func<LauncherSettings> settings) =>
        ct => GridDb.OpenRobustAsync(settings(), TimeoutSeconds, ct);
}
