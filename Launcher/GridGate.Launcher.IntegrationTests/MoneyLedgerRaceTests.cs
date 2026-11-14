using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Races the whole-branch review found (C1, C2, I1) and the ledger's honesty fixes (M1, M2).</summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerRaceTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Parallel_payouts_respect_the_daily_limit()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, DailyPayoutLimit: 50));
        await ledger.SetPayoutsAllowedAsync(A, true, Ct);
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            ledger.PayoutAsync(new PayoutRequest(Guid.NewGuid(), A, AccountRef.Resident(B), 10, Region, Guid.NewGuid(), "slot"), Ct))));

        Assert.Equal(5, results.Count(r => r.Outcome == MoneyOutcome.Ok));
        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { MoneyOutcome.Ok, MoneyOutcome.OverDailyLimit }));
        Assert.Equal(50, await db.Scalar("SELECT SUM(amount) FROM `money`.`entries` WHERE type = 'script_payout'"));
    }

    [Fact]
    public async Task Parallel_touches_grant_the_allowance_once()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, WeeklyAllowance: 10));
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct); // created: starting balance and the first allowance
        time.Advance(TimeSpan.FromDays(8));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ledger.BalanceAsync(AccountRef.Resident(A), Ct))));

        Assert.All(results, r => Assert.Equal(MoneyOutcome.Ok, r.Outcome));
        Assert.Equal(2, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'allowance'"));
        Assert.Equal(120, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{A}'"));
        Assert.True((await ledger.SelfCheckAsync(Ct)).Ok);
    }

    [Fact]
    public async Task Parallel_first_touches_create_one_account()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var c = Guid.NewGuid();
        await db.AddUser(c);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ledger.BalanceAsync(AccountRef.Resident(c), Ct))));

        Assert.All(results, r => Assert.Equal(MoneyOutcome.Ok, r.Outcome));
        Assert.All(results, r => Assert.Equal(100, r.FromBalance));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE agent_id = '{c}'"));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'starting_balance'"));
    }

    [Fact]
    public async Task Entry_after_balances_match_the_books()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, WeeklyAllowance: 10));

        // A new resident: the allowance moves the bank between the locks and the gift's own debit.
        await ledger.GiveAsync(AccountRef.Resident(A), 5, new StaffAction("Owner", "welcome"), Ct);

        Assert.Equal(await db.Scalar("SELECT balance FROM `money`.`accounts` WHERE id = 1"),
            await db.Scalar("SELECT from_after FROM `money`.`entries` WHERE type = 'staff_gift'"));
        Assert.Equal(await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{A}'"),
            await db.Scalar("SELECT to_after FROM `money`.`entries` WHERE type = 'staff_gift'"));
        // Every entry's after-balance for the bank, in order, follows the bank's own movements.
        Assert.Equal(0, await db.Scalar("""
            SELECT COUNT(*) FROM `money`.`entries` e
            WHERE e.from_id = 1 AND e.from_after <> -(SELECT SUM(x.amount) FROM `money`.`entries` x WHERE x.from_id = 1 AND x.id <= e.id)
            """));
    }

    [Fact]
    public async Task Reused_txn_for_another_request_is_refused()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var txn = Guid.NewGuid();
        var pay = new TransferRequest(txn, EntryType.PayAvatar, AccountRef.Resident(A), AccountRef.Resident(B), 10);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.TransferAsync(pay, Ct)).Outcome);

        Assert.Equal(MoneyOutcome.DuplicateTxn, (await ledger.TransferAsync(pay with { Amount = 11 }, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.DuplicateTxn,
            (await ledger.HoldAsync(new HoldRequest(txn, "object", AccountRef.Resident(A), AccountRef.Resident(B), 10, Region, null, null), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.TransferAsync(pay, Ct)).Outcome); // the same request again is still fine
        Assert.Equal(90, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{A}'"));
    }
}
