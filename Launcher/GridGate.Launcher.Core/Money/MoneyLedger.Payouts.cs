using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// A script paying someone from its object's owner (<c>llGiveMoney</c>, <c>llTransferLindenDollars</c>). The owner is always a
/// resident: visitors' objects never pay out (money design §7.2).
/// </summary>
public sealed record PayoutRequest(Guid TxnId, Guid OwnerId, AccountRef To, long Amount, Guid RegionId, Guid ObjectId, string ObjectName);

/// <summary>
/// A fee: <see cref="EntryType.UploadFee"/> and <see cref="EntryType.ClassifiedFee"/> go to Fees;
/// <see cref="EntryType.GroupJoinFee"/> goes to the group's founder (<paramref name="PayTo"/>), or to Fees without one.
/// </summary>
public sealed record ChargeRequest(Guid TxnId, string Type, AccountRef Payer, long Amount, Guid? PayTo);

public sealed partial class MoneyLedger
{
    private static readonly TimeSpan PayoutWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Pays out only when script payouts are on for the grid, the owner opted in, and the owner's payouts over the last 24
    /// hours stay within the daily limit.
    /// </summary>
    public Task<MoneyResult> PayoutAsync(PayoutRequest r, CancellationToken ct)
    {
        if (!AmountOk(r.Amount)) return Task.FromResult(MoneyResult.Of(MoneyOutcome.BadAmount));
        if (!settings().ScriptPayouts) return Task.FromResult(MoneyResult.Of(MoneyOutcome.NotAllowed));
        var owner = AccountRef.Resident(r.OwnerId);
        if (Same(owner, r.To)) return Task.FromResult(MoneyResult.Of(MoneyOutcome.NotAllowed));
        return Idempotent(r.TxnId, "payout", async (conn, tx) =>
        {
            if (await GridFrozenAsync(conn, tx, ct)) return MoneyResult.Of(MoneyOutcome.Frozen);
            var (payer, r1) = await ResolveAsync(conn, tx, owner, ct);
            if (payer is null) return MoneyResult.Of(r1);

            // Locking the owner first makes two payouts at once see each other's total.
            await LockAsync(conn, tx, payer.Id, ct);
            if (await ScalarAsync(conn, tx, "SELECT payouts_allowed FROM accounts WHERE id = @id", ct, ("@id", payer.Id)) != 1)
                return MoneyResult.Of(MoneyOutcome.NotAllowed);
            var paidToday = await ScalarAsync(conn, tx,
                "SELECT COALESCE(SUM(amount), 0) FROM entries WHERE type = @type AND from_id = @id AND at > @since",
                ct, ("@type", EntryType.ScriptPayout), ("@id", payer.Id), ("@since", Now - PayoutWindow));
            if (paidToday + r.Amount > settings().DailyPayoutLimit)
                return MoneyResult.Of(MoneyOutcome.OverDailyLimit);

            var (payee, r2) = await ResolveAsync(conn, tx, r.To, ct);
            if (payee is null) return MoneyResult.Of(r2);
            return await MoveBetweenAsync(conn, tx, payer.Id, payee.Id, r.Amount,
                new Entry(EntryType.ScriptPayout, r.RegionId, r.ObjectId, r.ObjectName), guarded: true, ct);
        }, ct);
    }

    /// <summary>A resident's "my scripts may pay out" switch (money design §9.5); creates their account if needed.</summary>
    public Task<MoneyResult> SetPayoutsAllowedAsync(Guid residentId, bool allowed, CancellationToken ct) =>
        RunAsync(async (conn, tx) =>
        {
            var (account, refusal) = await ResolveAsync(conn, tx, AccountRef.Resident(residentId), ct);
            if (account is null) return MoneyResult.Of(refusal);
            await ExecAsync(conn, tx, "UPDATE accounts SET payouts_allowed = @on WHERE id = @id", ct, ("@on", allowed ? 1 : 0), ("@id", account.Id));
            return MoneyResult.Of(MoneyOutcome.Ok);
        }, ct);

    /// <summary>Charges a fee; any other entry type is refused.</summary>
    public Task<MoneyResult> ChargeAsync(ChargeRequest r, CancellationToken ct)
    {
        if (r.Type is not (EntryType.UploadFee or EntryType.ClassifiedFee or EntryType.GroupJoinFee))
            return Task.FromResult(MoneyResult.Of(MoneyOutcome.NotAllowed));
        if (!AmountOk(r.Amount)) return Task.FromResult(MoneyResult.Of(MoneyOutcome.BadAmount));
        return Idempotent(r.TxnId, "charge", async (conn, tx) =>
        {
            if (await GridFrozenAsync(conn, tx, ct)) return MoneyResult.Of(MoneyOutcome.Frozen);
            var (payer, refusal) = await ResolveAsync(conn, tx, r.Payer, ct);
            if (payer is null) return MoneyResult.Of(refusal);

            var payeeId = MoneySchema.FeesId;
            if (r.Type == EntryType.GroupJoinFee && r.PayTo is { } founder && founder != r.Payer.AgentId
                && (await ResolveAsync(conn, tx, AccountRef.Resident(founder), ct)).Account is { } founderAccount)
                payeeId = founderAccount.Id;
            return await MoveBetweenAsync(conn, tx, payer.Id, payeeId, r.Amount, new Entry(r.Type), guarded: true, ct);
        }, ct);
    }
}
