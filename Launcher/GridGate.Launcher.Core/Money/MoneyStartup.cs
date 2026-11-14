using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GridGate.Launcher.Core.Money;

/// <summary>Gets the money database ready when the grid's database starts (money design §7.1).</summary>
public static class MoneyStartup
{
    public const int PasswordLength = 48;

    /// <summary>
    /// When money is on: gives moneyuser a password the first time (saved through <paramref name="update"/>), creates the
    /// money database and its user, and creates or updates its tables. When money is Off it does nothing and returns
    /// <paramref name="s"/>.
    /// </summary>
    public static async Task<LauncherSettings> PrepareAsync(
        LauncherSettings s, Func<Func<LauncherSettings, LauncherSettings>, LauncherSettings> update, MoneyMode effective, CancellationToken ct)
    {
        if (effective == MoneyMode.Off) return s;
        if (s.Db.MoneyPassword == "")
            s = update(x => x.Db.MoneyPassword == "" ? x with { Db = x.Db with { MoneyPassword = Secrets.NewPassword(PasswordLength) } } : x);
        await new DatabaseProvisioner(s).EnsureAsync(ct);
        await using var conn = await GridDb.OpenMoneyAsync(s, 10, ct);
        await MoneySchema.EnsureAsync(conn, ct);
        return s;
    }

    /// <summary>
    /// <see cref="PrepareAsync"/>, but a failure is logged and leaves money unavailable instead of stopping the grid (money
    /// design §5). Returns the settings as they are after whatever succeeded.
    /// </summary>
    public static async Task<LauncherSettings> TryPrepareAsync(
        LauncherSettings s, Func<Func<LauncherSettings, LauncherSettings>, LauncherSettings> update, MoneyMode effective, ILogger log, CancellationToken ct)
    {
        var latest = s;
        try
        {
            return await PrepareAsync(s, change => latest = update(change), effective, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError("The money database couldn't be prepared ({Error}); payments are refused until the next start.", e.GetType().Name);
            return latest;
        }
    }
}
