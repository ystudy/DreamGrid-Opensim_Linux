using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Staff operations, totals, history and the self-check (money plan 1, task 6).</summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerStaffTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid V = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly StaffAction Staff = new("Owner Person", "event prize");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<long> Of(MoneyTestDatabase db, Guid id, string home = "") =>
        db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{id}' AND home = '{home}'");

    private static TransferRequest Pay(Guid from, Guid to, long amount) =>
        new(Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(from), AccountRef.Resident(to), amount);

    [Fact]
    public async Task Give_and_take_need_a_reason()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.Equal(MoneyOutcome.BadAmount, (await ledger.GiveAsync(AccountRef.Resident(A), 5, Staff with { Reason = " " }, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.GiveAsync(AccountRef.Resident(A), 5, Staff, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.TakeAsync(AccountRef.Resident(A), 2, Staff, Ct)).Outcome);

        Assert.Equal(103, await Of(db, A));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'staff_gift' AND staff = 'Owner Person' AND reason = 'event prize'"));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'staff_take' AND to_id = 1"));
    }

    [Fact]
    public async Task Take_cannot_go_below_zero()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);

        Assert.Equal(MoneyOutcome.NotEnough, (await ledger.TakeAsync(AccountRef.Resident(A), 101, Staff, Ct)).Outcome);
        Assert.Equal(100, await Of(db, A));
    }

    [Fact]
    public async Task Refund_reverses_once()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var paid = await ledger.TransferAsync(Pay(A, B, 30), Ct);

        Assert.Equal(MoneyOutcome.Ok, (await ledger.RefundAsync(paid.EntryId!.Value, Staff, Ct)).Outcome);
        Assert.Equal(100, await Of(db, A));
        Assert.Equal(100, await Of(db, B));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'refund' AND reverses = {paid.EntryId}"));

        Assert.Equal(MoneyOutcome.HoldClosed, (await ledger.RefundAsync(paid.EntryId!.Value, Staff, Ct)).Outcome);
        Assert.Equal(100, await Of(db, A));
    }

    [Fact]
    public async Task Merge_moves_all_and_freezes_the_old()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var oldHome = new AccountRef(V, "http://old.example:8002");
        var newHome = new AccountRef(V, "http://new.example:8002");
        await ledger.GiveAsync(oldHome, 25, Staff, Ct);

        Assert.Equal(MoneyOutcome.Ok, (await ledger.MergeWalletsAsync(oldHome, newHome, Staff, Ct)).Outcome);

        Assert.Equal(0, await Of(db, V, oldHome.Home));
        Assert.Equal(25, await Of(db, V, newHome.Home));
        Assert.Equal(1, await db.Scalar($"SELECT frozen FROM `money`.`accounts` WHERE agent_id = '{V}' AND home = '{oldHome.Home}'"));
    }

    [Fact]
    public async Task Fees_can_be_moved_out()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.UploadFee, AccountRef.Resident(A), 10, null), Ct);

        Assert.Equal(MoneyOutcome.Ok, (await ledger.MoveFeesAsync(AccountRef.Resident(B), 10, Staff, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NotEnough, (await ledger.MoveFeesAsync(AccountRef.Resident(B), 1, Staff, Ct)).Outcome);
        Assert.Equal(110, await Of(db, B));
    }

    [Fact]
    public async Task Freeze_switches_are_read_back()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);

        await ledger.SetFrozenAsync(true, "checking something", Ct);
        Assert.Equal((true, "checking something"), await ledger.FrozenAsync(Ct));
        Assert.Equal(MoneyOutcome.Frozen, (await ledger.TransferAsync(Pay(A, B, 1), Ct)).Outcome);

        await ledger.SetFrozenAsync(false, "", Ct);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.TransferAsync(Pay(A, B, 1), Ct)).Outcome);

        await ledger.SetAccountFrozenAsync(AccountRef.Resident(A), true, Ct);
        Assert.Equal(MoneyOutcome.AccountFrozen, (await ledger.TransferAsync(Pay(A, B, 1), Ct)).Outcome);
    }

    [Fact]
    public async Task Totals_add_up()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.TransferAsync(Pay(A, B, 30), Ct);
        await ledger.GiveAsync(new AccountRef(V, "http://other.example:8002"), 7, Staff, Ct);
        await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.UploadFee, AccountRef.Resident(A), 5, null), Ct);
        await ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), "object", AccountRef.Resident(B), AccountRef.Resident(A), 4, null, null, null), Ct);

        var t = await ledger.TotalsAsync(Ct);

        Assert.Equal(207, t.InExistence);
        Assert.Equal(191, t.Residents);
        Assert.Equal(7, t.Visitors);
        Assert.Equal(5, t.Fees);
        Assert.Equal(4, t.Holding);
        Assert.Equal(t.InExistence, t.Residents + t.Visitors + t.Fees + t.Holding);
    }

    [Fact]
    public async Task Self_check_passes_on_clean_books()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.TransferAsync(Pay(A, B, 30), Ct);

        var r = await ledger.SelfCheckAsync(Ct);

        Assert.True(r.Ok);
        Assert.Equal(0, r.Sum);
        Assert.Empty(r.Mismatches);
        Assert.False((await ledger.FrozenAsync(Ct)).Frozen);
    }

    [Fact]
    public async Task Self_check_catches_a_tampered_balance()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.TransferAsync(Pay(A, B, 30), Ct);
        var aId = await db.Scalar($"SELECT id FROM `money`.`accounts` WHERE agent_id = '{A}'");
        await db.Sql($"UPDATE `money`.`accounts` SET balance = balance + 5 WHERE id = {aId}");

        var r = await ledger.SelfCheckAsync(Ct);

        Assert.False(r.Ok);
        Assert.Equal(5, r.Sum);
        Assert.Contains(r.Mismatches, m => m.AccountId == aId && m.Stored == 75 && m.FromEntries == 70);
        Assert.True((await ledger.FrozenAsync(Ct)).Frozen);
    }

    [Fact]
    public async Task History_is_newest_first()
    {
        var (ledger, _, time) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.TransferAsync(Pay(A, B, 1), Ct);
        time.Advance(TimeSpan.FromSeconds(1));
        await ledger.TransferAsync(Pay(B, A, 2), Ct);

        var h = await ledger.HistoryAsync(AccountRef.Resident(A), 10, Ct);

        Assert.Equal([EntryType.PayAvatar, EntryType.PayAvatar, EntryType.StartingBalance], h.Select(l => l.Type));
        Assert.Equal(2, h[0].Amount);
        Assert.Equal(101, h[0].BalanceAfter);
        Assert.Equal(-1, h[1].Amount);
        Assert.Equal(99, h[1].BalanceAfter);
        Assert.Equal(AccountRef.Resident(B), h[0].Other);
    }
}
