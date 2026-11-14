using GridGate.Launcher.Core.Money;
using Microsoft.Extensions.Time.Testing;
using MySqlConnector;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Ledger accounts and guarded transfers (money plan 1, task 3), on a real MariaDB.</summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerTransferTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid V = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private const string OtherGrid = "http://other.example:8002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A ledger in Play mode over a fresh database, with A and B as local residents.</summary>
    internal static async Task<(MoneyLedger Ledger, MoneyTestDatabase Db, FakeTimeProvider Time)> Setup(
        MoneyMariaDb maria, MoneySettings? settings = null, MoneyMode mode = MoneyMode.Play)
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await db.AddUser(A);
        await db.AddUser(B);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var s = settings ?? new MoneySettings(Mode: mode);
        var ledger = new MoneyLedger(db.OpenMoney, () => s, () => mode, LocalUsers(db), time);
        return (ledger, db, time);
    }

    /// <summary>Whether an id is in robust.UserAccounts, read the way PresenceCheck.IsLocalUserAsync does.</summary>
    internal static Func<Guid, CancellationToken, Task<bool>> LocalUsers(MoneyTestDatabase db) => async (id, ct) =>
    {
        await using var conn = await db.OpenRobust(ct);
        await using var cmd = new MySqlCommand("SELECT 1 FROM UserAccounts WHERE PrincipalID = @id", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return await cmd.ExecuteScalarAsync(ct) is not null;
    };

    private static TransferRequest Pay(Guid from, Guid to, long amount, Guid? txn = null) =>
        new(txn ?? Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(from), AccountRef.Resident(to), amount);

    private static Task<long> Balance(MoneyTestDatabase db, Guid id, string home = "") =>
        db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{id}' AND home = '{home}'");

    [Fact]
    public async Task New_resident_gets_starting_balance()
    {
        var (ledger, db, _) = await Setup(maria);

        var r = await ledger.BalanceAsync(AccountRef.Resident(A), Ct);

        Assert.Equal(MoneyOutcome.Ok, r.Outcome);
        Assert.Equal(100, r.FromBalance);
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'starting_balance' AND from_id = 1"));
        Assert.Equal(-100, await db.Scalar("SELECT balance FROM `money`.`accounts` WHERE id = 1"));
    }

    [Fact]
    public async Task Unknown_resident_id_gets_NoWallet()
    {
        var (ledger, db, _) = await Setup(maria);
        var stranger = Guid.NewGuid();

        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.BalanceAsync(AccountRef.Resident(stranger), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.TransferAsync(Pay(A, stranger, 5), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.TransferAsync(Pay(stranger, A, 5), Ct)).Outcome);
        Assert.Equal(0, await db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE agent_id = '{stranger}'"));
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type <> 'starting_balance'"));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "system:bank")]
    [InlineData("00000000-0000-0000-0000-000000000000", "system:holding")]
    [InlineData("00000000-0000-0000-0000-000000000000", "")]
    [InlineData("a0000000-0000-0000-0000-00000000000a", "system:fees")]
    public async Task System_accounts_cannot_be_named(string agent, string home)
    {
        var (ledger, db, _) = await Setup(maria);
        var system = new AccountRef(Guid.Parse(agent), home);

        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.TransferAsync(new TransferRequest(Guid.NewGuid(), EntryType.PayAvatar, system, AccountRef.Resident(A), 5), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.TransferAsync(new TransferRequest(Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(A), system, 5), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.BalanceAsync(system, Ct)).Outcome);
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'pay_avatar'"));
    }

    [Fact]
    public async Task Paying_yourself_is_refused()
    {
        var (ledger, _, _) = await Setup(maria);

        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.TransferAsync(Pay(A, A, 5), Ct)).Outcome);
    }

    [Fact]
    public async Task Visitor_homes_are_compared_without_case_or_trailing_slash()
    {
        var (ledger, db, _) = await Setup(maria);
        await ledger.TransferAsync(new TransferRequest(Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(A), new AccountRef(V, "HTTP://Other.Example:8002/"), 5), Ct);

        Assert.Equal(5, (await ledger.BalanceAsync(new AccountRef(V, OtherGrid), Ct)).FromBalance);
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE agent_id = '{V}'"));
    }

    [Fact]
    public async Task Visitor_starts_at_zero()
    {
        var (ledger, _, _) = await Setup(maria);

        var r = await ledger.BalanceAsync(new AccountRef(V, OtherGrid), Ct);

        Assert.Equal(MoneyOutcome.Ok, r.Outcome);
        Assert.Equal(0, r.FromBalance);
    }

    [Fact]
    public async Task Visitor_without_wallets_gets_NoWallet()
    {
        var (ledger, _, _) = await Setup(maria, new MoneySettings(Mode: MoneyMode.Play, VisitorWallets: false));

        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.BalanceAsync(new AccountRef(V, OtherGrid), Ct)).Outcome);
    }

    [Fact]
    public async Task Transfer_moves_coins_and_writes_one_entry()
    {
        var (ledger, db, _) = await Setup(maria);

        var r = await ledger.TransferAsync(Pay(A, B, 30) with { ObjectName = "tip jar" }, Ct);

        Assert.Equal(MoneyOutcome.Ok, r.Outcome);
        Assert.Equal(70, r.FromBalance);
        Assert.Equal(130, r.ToBalance);
        Assert.Equal(70, await Balance(db, A));
        Assert.Equal(130, await Balance(db, B));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'pay_avatar' AND amount = 30 AND from_after = 70 AND to_after = 130 AND object_name = 'tip jar'"));
    }

    [Fact]
    public async Task Transfer_more_than_balance_is_NotEnough()
    {
        var (ledger, db, _) = await Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);

        var r = await ledger.TransferAsync(Pay(A, B, 101), Ct);

        Assert.Equal(MoneyOutcome.NotEnough, r.Outcome);
        Assert.Equal(100, await Balance(db, A));
        Assert.Equal(100, await Balance(db, B));
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'pay_avatar'"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1_000_001)]
    public async Task Bad_amounts_change_nothing(long amount)
    {
        var (ledger, db, _) = await Setup(maria);

        var r = await ledger.TransferAsync(Pay(A, B, amount), Ct);

        Assert.Equal(MoneyOutcome.BadAmount, r.Outcome);
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries`"));
    }

    [Fact]
    public async Task Frozen_grid_refuses_transfers()
    {
        var (ledger, db, _) = await Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await db.Sql("UPDATE `money`.`state` SET value = '1' WHERE name = 'frozen'");

        Assert.Equal(MoneyOutcome.Frozen, (await ledger.TransferAsync(Pay(A, B, 5), Ct)).Outcome);
        Assert.Equal(100, await Balance(db, A));
    }

    [Fact]
    public async Task Frozen_account_refuses_its_debits()
    {
        var (ledger, db, _) = await Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await db.Sql($"UPDATE `money`.`accounts` SET frozen = 1 WHERE agent_id = '{A}'");

        Assert.Equal(MoneyOutcome.AccountFrozen, (await ledger.TransferAsync(Pay(A, B, 5), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.TransferAsync(Pay(B, A, 5), Ct)).Outcome);
    }

    [Fact]
    public async Task Repeated_txn_pays_once()
    {
        var (ledger, db, _) = await Setup(maria);
        var txn = Guid.NewGuid();

        var first = await ledger.TransferAsync(Pay(A, B, 30, txn), Ct);
        var second = await ledger.TransferAsync(Pay(A, B, 30, txn), Ct);

        Assert.Equal(first, second);
        Assert.Equal(70, await Balance(db, A));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'pay_avatar'"));
    }

    [Fact]
    public async Task Parallel_transfers_never_overdraw()
    {
        var (ledger, db, _) = await Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => ledger.TransferAsync(Pay(A, B, 3), Ct))));

        Assert.Equal(33, results.Count(r => r.Outcome == MoneyOutcome.Ok));
        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { MoneyOutcome.Ok, MoneyOutcome.NotEnough }));
        Assert.Equal(1, await Balance(db, A));
        Assert.Equal(0, await db.Scalar("SELECT SUM(balance) FROM `money`.`accounts`"));
    }

    [Fact]
    public async Task Crossing_transfers_all_complete()
    {
        var (ledger, db, _) = await Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);

        var work = Enumerable.Range(0, 40).Select(i => Task.Run(() => i % 2 == 0
            ? ledger.TransferAsync(Pay(A, B, 1), Ct)
            : ledger.TransferAsync(Pay(B, A, 1), Ct)));
        var results = await Task.WhenAll(work);

        Assert.All(results, r => Assert.Equal(MoneyOutcome.Ok, r.Outcome));
        Assert.Equal(100, await Balance(db, A));
        Assert.Equal(100, await Balance(db, B));
    }

    [Fact]
    public async Task Only_the_bank_goes_negative()
    {
        var (ledger, db, _) = await Setup(maria);
        await ledger.TransferAsync(Pay(A, B, 40), Ct);
        await ledger.BalanceAsync(new AccountRef(V, OtherGrid), Ct);

        Assert.Equal(-200, await db.Scalar("SELECT balance FROM `money`.`accounts` WHERE id = 1"));
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`accounts` WHERE id <> 1 AND balance < 0"));
        Assert.Equal(0, await db.Scalar("SELECT SUM(balance) FROM `money`.`accounts`"));
    }

    [Fact]
    public async Task Long_object_name_is_trimmed()
    {
        var (ledger, db, _) = await Setup(maria);
        var name = string.Concat(Enumerable.Repeat("🎵", 300));

        var r = await ledger.TransferAsync(Pay(A, B, 1) with { ObjectName = name }, Ct);

        Assert.Equal(MoneyOutcome.Ok, r.Outcome);
        var stored = await db.Text("SELECT object_name FROM `money`.`entries` WHERE type = 'pay_avatar'");
        Assert.NotNull(stored);
        Assert.True(stored!.Length <= 255);
        Assert.False(char.IsHighSurrogate(stored[^1]));
    }

    [Fact]
    public async Task Unreachable_database_reads_as_unavailable()
    {
        var (_, db, time) = await Setup(maria);
        var s = new MoneySettings(Mode: MoneyMode.Play);
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var ledger = new MoneyLedger(ct => Core.Database.GridDb.OpenMoneyAsync(closed, 2, ct), () => s, () => MoneyMode.Play,
            (_, _) => Task.FromResult(true), time);

        Assert.Equal(MoneyOutcome.Unavailable, (await ledger.BalanceAsync(AccountRef.Resident(A), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.Unavailable, (await ledger.TransferAsync(Pay(A, B, 1), Ct)).Outcome);
    }

    [Fact]
    public async Task Allowance_once_a_week()
    {
        var (ledger, _, time) = await Setup(maria, new MoneySettings(Mode: MoneyMode.Play, WeeklyAllowance: 10));

        Assert.Equal(110, (await ledger.BalanceAsync(AccountRef.Resident(A), Ct)).FromBalance);
        time.Advance(TimeSpan.FromDays(6));
        Assert.Equal(110, (await ledger.BalanceAsync(AccountRef.Resident(A), Ct)).FromBalance);
        time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(120, (await ledger.BalanceAsync(AccountRef.Resident(A), Ct)).FromBalance);
    }
}
