using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Script payouts and fees (money plan 1, task 5).</summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerPayoutTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PayoutRequest Payout(Guid owner, long amount) =>
        new(Guid.NewGuid(), owner, AccountRef.Resident(B), amount, Region, Guid.NewGuid(), "slot machine");

    private static Task<long> Of(MoneyTestDatabase db, Guid id) => db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{id}' AND home = ''");

    [Fact]
    public async Task Payout_without_opt_in_is_refused()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.PayoutAsync(Payout(A, 10), Ct)).Outcome);
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'script_payout'"));
    }

    [Fact]
    public async Task Payout_from_unknown_owner_is_NoWallet()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var stranger = Guid.NewGuid();

        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.PayoutAsync(Payout(stranger, 10), Ct)).Outcome);
        Assert.Equal(0, await db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE agent_id = '{stranger}'"));
    }

    [Fact]
    public async Task Payout_after_opt_in_pays()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.SetPayoutsAllowedAsync(A, true, Ct)).Outcome);

        var r = await ledger.PayoutAsync(Payout(A, 10), Ct);

        Assert.Equal(MoneyOutcome.Ok, r.Outcome);
        Assert.Equal(110, await Of(db, B));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'script_payout' AND object_name = 'slot machine'"));
    }

    [Fact]
    public async Task Payout_switch_off_refuses_everyone()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, ScriptPayouts: false));
        await ledger.SetPayoutsAllowedAsync(A, true, Ct);

        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.PayoutAsync(Payout(A, 10), Ct)).Outcome);
    }

    [Fact]
    public async Task Payout_over_daily_limit_is_refused()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, DailyPayoutLimit: 50));
        await ledger.SetPayoutsAllowedAsync(A, true, Ct);

        Assert.Equal(MoneyOutcome.Ok, (await ledger.PayoutAsync(Payout(A, 30), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.OverDailyLimit, (await ledger.PayoutAsync(Payout(A, 30), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.PayoutAsync(Payout(A, 20), Ct)).Outcome);
    }

    [Fact]
    public async Task Payout_limit_is_rolling_24_hours()
    {
        var (ledger, _, time) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, DailyPayoutLimit: 50));
        await ledger.SetPayoutsAllowedAsync(A, true, Ct);
        time.SetUtcNow(new DateTimeOffset(2026, 11, 13, 23, 59, 0, TimeSpan.Zero));
        Assert.Equal(MoneyOutcome.Ok, (await ledger.PayoutAsync(Payout(A, 30), Ct)).Outcome);

        time.SetUtcNow(new DateTimeOffset(2026, 11, 14, 0, 1, 0, TimeSpan.Zero));
        Assert.Equal(MoneyOutcome.OverDailyLimit, (await ledger.PayoutAsync(Payout(A, 30), Ct)).Outcome);

        time.SetUtcNow(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(MoneyOutcome.Ok, (await ledger.PayoutAsync(Payout(A, 30), Ct)).Outcome);
    }

    [Fact]
    public async Task Upload_fee_goes_to_fees()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        var r = await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.UploadFee, AccountRef.Resident(A), 5, null), Ct);

        Assert.Equal(MoneyOutcome.Ok, r.Outcome);
        Assert.Equal(95, await Of(db, A));
        Assert.Equal(5, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE id = {MoneySchema.FeesId}"));
    }

    [Fact]
    public async Task Group_join_fee_goes_to_the_founder()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        var r = await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.GroupJoinFee, AccountRef.Resident(A), 20, B), Ct);

        Assert.Equal(MoneyOutcome.Ok, r.Outcome);
        Assert.Equal(120, await Of(db, B));
    }

    [Fact]
    public async Task Group_join_fee_without_founder_goes_to_fees()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.GroupJoinFee, AccountRef.Resident(A), 20, null), Ct);

        Assert.Equal(20, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE id = {MoneySchema.FeesId}"));
    }

    [Fact]
    public async Task Unknown_charge_type_is_refused()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);

        var r = await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.StaffGift, AccountRef.Resident(A), 20, null), Ct);

        Assert.Equal(MoneyOutcome.NotAllowed, r.Outcome);
    }
}
