using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Purchase holds (money plan 1, task 4).</summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerHoldTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HoldRequest Buy(long amount, string kind = "object") =>
        new(Guid.NewGuid(), kind, AccountRef.Resident(A), AccountRef.Resident(B), amount, Region, Guid.NewGuid(), "dress");

    private static Task<long> Balance(MoneyTestDatabase db, long id) => db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE id = {id}");

    private static Task<long> Of(MoneyTestDatabase db, Guid id) => db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{id}'");

    [Fact]
    public async Task Complete_pays_the_seller()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        var hold = await ledger.HoldAsync(Buy(40), Ct);
        Assert.Equal(MoneyOutcome.Ok, hold.Outcome);
        Assert.NotNull(hold.HoldId);
        Assert.Equal(60, await Of(db, A));
        Assert.Equal(40, await Balance(db, MoneySchema.HoldingId));

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Equal(0, await Balance(db, MoneySchema.HoldingId));
        Assert.Equal(140, await Of(db, B));
        Assert.Equal("completed", await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}"));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'hold_completed' AND hold_id = {hold.HoldId}"));
    }

    [Fact]
    public async Task Return_gives_the_buyer_back()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(40), Ct);

        var back = await ledger.ReturnHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, back.Outcome);
        Assert.Equal(100, await Of(db, A));
        Assert.Equal("returned", await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}"));
    }

    [Fact]
    public async Task Settling_works_while_frozen()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(40), Ct);
        await db.Sql("UPDATE `money`.`state` SET value = '1' WHERE name = 'frozen'; UPDATE `money`.`accounts` SET frozen = 1;");

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Equal(140, await Of(db, B));
    }

    [Fact]
    public async Task Second_settle_is_HoldClosed()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(40), Ct);
        await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        var again = await ledger.ReturnHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.HoldClosed, again.Outcome);
        Assert.Equal(60, await Of(db, A));
        Assert.Equal(140, await Of(db, B));
    }

    [Fact]
    public async Task Unknown_hold_is_UnknownHold()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.Equal(MoneyOutcome.UnknownHold, (await ledger.CompleteHoldAsync(Guid.NewGuid(), 999, Ct)).Outcome);
    }

    [Fact]
    public async Task Expired_holds_return_after_five_minutes()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.HoldAsync(Buy(40), Ct);

        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, await ledger.ReturnExpiredHoldsAsync(Ct));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await ledger.ReturnExpiredHoldsAsync(Ct));

        Assert.Equal(100, await Of(db, A));
    }

    [Fact]
    public async Task Hold_survives_a_new_ledger_instance_and_expires()
    {
        var (first, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        await first.HoldAsync(Buy(40), Ct);
        var s = new MoneySettings(Mode: MoneyMode.Play);
        var second = new MoneyLedger(db.OpenMoney, () => s, () => MoneyMode.Play, MoneyLedgerTransferTests.LocalUsers(db), time);

        time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(1, await second.ReturnExpiredHoldsAsync(Ct));
        Assert.Equal(100, await Of(db, A));
    }

    [Fact]
    public async Task Hold_needs_enough_coins()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.Equal(MoneyOutcome.NotEnough, (await ledger.HoldAsync(Buy(101), Ct)).Outcome);
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`holds`"));
    }

    [Fact]
    public async Task Hold_region_is_remembered()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(10), Ct);

        Assert.Equal(Region, await ledger.HoldRegionAsync(hold.HoldId!.Value, Ct));
        Assert.Null(await ledger.HoldRegionAsync(12345, Ct));
    }
}
