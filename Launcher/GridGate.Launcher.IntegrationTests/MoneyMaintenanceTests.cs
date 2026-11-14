using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>The background self-check and hold sweep, and getting the money database ready at start (money plan 1, task 10).</summary>
[Collection(GridCollection.Name)]
public sealed class MoneyMaintenanceTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Waits for <paramref name="condition"/>, calling <paramref name="tick"/> between tries. .NET 10 starts a
    /// BackgroundService's work on another thread, so its timer may not exist yet when a test first moves the fake clock;
    /// moving it on every try reaches the timer whenever it was made.
    /// </summary>
    private static async Task Eventually(Func<Task<bool>> condition, string what, Action? tick = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            tick?.Invoke();
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Expired_hold_is_returned_by_the_sweep()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), "object", AccountRef.Resident(A), AccountRef.Resident(B), 40, null, null, null), Ct);
        using var maintenance = new MoneyMaintenance(ledger, () => MoneyMode.Play, time, NullLogger<MoneyMaintenance>.Instance);
        await maintenance.StartAsync(Ct);

        time.Advance(TimeSpan.FromMinutes(6));

        await Eventually(async () => await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}") == "returned", "the hold is returned",
            () => time.Advance(MoneyMaintenance.SweepEvery));
        await maintenance.StopAsync(Ct);
    }

    [Fact]
    public async Task Startup_self_check_freezes_tampered_books()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await db.Sql($"UPDATE `money`.`accounts` SET balance = balance + 5 WHERE agent_id = '{A}'");
        using var maintenance = new MoneyMaintenance(ledger, () => MoneyMode.Play, time, NullLogger<MoneyMaintenance>.Instance);

        await maintenance.StartAsync(Ct);

        await Eventually(async () => (await ledger.FrozenAsync(Ct)).Frozen, "the grid is frozen");
        await maintenance.StopAsync(Ct);
    }

    /// <summary>A ledger over <paramref name="db"/> whose database answers only once <paramref name="up"/> says so.</summary>
    private static MoneyLedger Flaky(MoneyTestDatabase db, Func<bool> up, Func<CancellationToken, Task<MySqlConnector.MySqlConnection>> down, Microsoft.Extensions.Time.Testing.FakeTimeProvider time)
    {
        var s = new MoneySettings(Mode: MoneyMode.Play);
        return new MoneyLedger(ct => up() ? db.OpenMoney(ct) : down(ct), () => s, () => MoneyMode.Play, MoneyLedgerTransferTests.LocalUsers(db), time);
    }

    [Fact]
    public async Task Self_check_retries_until_it_runs()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await db.Sql($"UPDATE `money`.`accounts` SET balance = balance + 5 WHERE agent_id = '{A}'");
        var up = false;
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var flaky = Flaky(db, () => up, ct => Core.Database.GridDb.OpenMoneyAsync(closed, 2, ct), time);
        using var maintenance = new MoneyMaintenance(flaky, () => MoneyMode.Play, time, NullLogger<MoneyMaintenance>.Instance);
        await maintenance.StartAsync(Ct);
        await Task.Delay(300, Ct); // the start-up check finds the database down

        up = true;

        await Eventually(async () => (await ledger.FrozenAsync(Ct)).Frozen, "the self-check runs once the database is up",
            () => time.Advance(MoneyMaintenance.SweepEvery));
        await maintenance.StopAsync(Ct);
    }

    [Fact]
    public async Task Maintenance_survives_unexpected_errors()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await db.Sql($"UPDATE `money`.`accounts` SET balance = balance + 5 WHERE agent_id = '{A}'");
        var up = false;
        var flaky = Flaky(db, () => up, _ => throw new InvalidOperationException("something unexpected"), time);
        using var maintenance = new MoneyMaintenance(flaky, () => MoneyMode.Play, time, NullLogger<MoneyMaintenance>.Instance);
        await maintenance.StartAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.False(maintenance.ExecuteTask!.IsCompleted, "maintenance stopped after an unexpected error");

        up = true;

        await Eventually(async () => (await ledger.FrozenAsync(Ct)).Frozen, "maintenance keeps going after an unexpected error",
            () => time.Advance(MoneyMaintenance.SweepEvery));
        await maintenance.StopAsync(Ct);
    }

    [Fact]
    public async Task Failed_preparation_does_not_throw()
    {
        var db = await maria.FreshAsync();
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort(), Db = db.Settings.Db with { MoneyPassword = "already-set" } };

        var after = await MoneyStartup.TryPrepareAsync(closed, f => f(closed), MoneyMode.Play, NullLogger.Instance, Ct);

        Assert.Same(closed, after);
    }

    [Fact]
    public async Task Off_does_nothing()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), "object", AccountRef.Resident(A), AccountRef.Resident(B), 40, null, null, null), Ct);
        using var maintenance = new MoneyMaintenance(ledger, () => MoneyMode.Off, time, NullLogger<MoneyMaintenance>.Instance);
        await maintenance.StartAsync(Ct);

        time.Advance(TimeSpan.FromMinutes(6));
        time.Advance(MoneyMaintenance.SweepEvery);
        await Task.Delay(500, Ct);

        Assert.Equal("open", await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}"));
        await maintenance.StopAsync(Ct);
    }

    [Fact]
    public async Task Start_makes_password_database_and_schema_when_money_is_on()
    {
        var db = await maria.FreshAsync();
        await db.Sql("DROP DATABASE `money`");
        var saved = db.Settings with { Db = db.Settings.Db with { MoneyPassword = "" } };
        LauncherSettings Update(Func<LauncherSettings, LauncherSettings> change) => saved = change(saved);

        try
        {
            var after = await MoneyStartup.PrepareAsync(saved, Update, MoneyMode.Play, Ct);

            Assert.Equal(48, after.Db.MoneyPassword.Length);
            Assert.Equal(after.Db.MoneyPassword, saved.Db.MoneyPassword);
            Assert.Equal(3, await new MoneyTestDatabase(after).Scalar("SELECT COUNT(*) FROM `money`.`accounts`"));
            await using var conn = await Core.Database.GridDb.OpenMoneyAsync(after, 10, Ct); // moneyuser has the new password
        }
        finally
        {
            // The class's other tests use the fixture's moneyuser password.
            await new Core.Database.DatabaseProvisioner(db.Settings).EnsureAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_leaves_money_alone_when_off()
    {
        var db = await maria.FreshAsync();
        await db.Sql("DROP DATABASE `money`");
        var saved = db.Settings with { Db = db.Settings.Db with { MoneyPassword = "" } };
        var updates = 0;

        var after = await MoneyStartup.PrepareAsync(saved, f => { updates++; return f(saved); }, MoneyMode.Off, Ct);

        Assert.Same(saved, after);
        Assert.Equal(0, updates);
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'money'"));
    }
}
