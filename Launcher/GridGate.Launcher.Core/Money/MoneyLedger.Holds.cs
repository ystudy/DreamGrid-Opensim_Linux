using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>A purchase's hold (money design §7.2): the buyer's coins wait in Holding until delivery succeeds or fails.</summary>
public sealed record HoldRequest(
    Guid TxnId,
    string Kind,
    AccountRef Buyer,
    AccountRef Seller,
    long Amount,
    Guid? RegionId,
    Guid? ObjectId,
    string? ObjectName);

/// <summary>A ledger read that couldn't reach the database.</summary>
public sealed class MoneyUnavailableException(Exception inner) : Exception("The money database can't be reached.", inner);

public sealed partial class MoneyLedger
{
    /// <summary>A hold neither completed nor returned within this time goes back to the buyer on its own.</summary>
    public static readonly TimeSpan HoldLifetime = TimeSpan.FromMinutes(5);

    private sealed record HoldRow(long Id, long BuyerId, long SellerId, long Amount, string Kind, Guid? RegionId, Guid? ObjectId, string? ObjectName, string State);

    /// <summary>Moves the buyer's coins into Holding and opens a hold; refused like a payment (freezes, balance).</summary>
    public Task<MoneyResult> HoldAsync(HoldRequest r, CancellationToken ct)
    {
        if (!AmountOk(r.Amount) || r.Kind is not ("object" or "land")) return Task.FromResult(MoneyResult.Of(MoneyOutcome.BadAmount));
        if (Same(r.Buyer, r.Seller)) return Task.FromResult(MoneyResult.Of(MoneyOutcome.NotAllowed));
        return Idempotent(r.TxnId, "hold", r, async (conn, tx) =>
        {
            if (await GridFrozenAsync(conn, tx, ct)) return MoneyResult.Of(MoneyOutcome.Frozen);
            var (buyer, r1) = await ResolveAsync(conn, tx, r.Buyer, ct);
            if (buyer is null) return MoneyResult.Of(r1);
            var (seller, r2) = await ResolveAsync(conn, tx, r.Seller, ct);
            if (seller is null) return MoneyResult.Of(r2);

            await using (var insert = new MySqlCommand("""
                INSERT INTO holds (buyer_id, seller_id, amount, kind, region_id, object_id, object_name, created, state)
                VALUES (@buyer, @seller, @amt, @kind, @region, @object, @name, @now, 'open')
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("@buyer", buyer.Id);
                insert.Parameters.AddWithValue("@seller", seller.Id);
                insert.Parameters.AddWithValue("@amt", r.Amount);
                insert.Parameters.AddWithValue("@kind", r.Kind);
                insert.Parameters.AddWithValue("@region", r.RegionId?.ToString());
                insert.Parameters.AddWithValue("@object", r.ObjectId?.ToString());
                insert.Parameters.AddWithValue("@name", Trim(r.ObjectName, 255));
                insert.Parameters.AddWithValue("@now", Now);
                await insert.ExecuteNonQueryAsync(ct);
                var holdId = insert.LastInsertedId;
                var moved = await MoveBetweenAsync(conn, tx, buyer.Id, MoneySchema.HoldingId, r.Amount,
                    new Entry(EntryType.Hold, r.RegionId, r.ObjectId, r.ObjectName, HoldId: holdId), guarded: true, ct);
                return moved.Outcome == MoneyOutcome.Ok ? moved with { HoldId = holdId } : moved;
            }
        }, ct);
    }

    /// <summary>Delivery succeeded: Holding pays the seller. Not blocked by freezes.</summary>
    public Task<MoneyResult> CompleteHoldAsync(Guid txnId, long holdId, CancellationToken ct) =>
        Idempotent(txnId, "hold_complete", holdId, (conn, tx) => SettleAsync(conn, tx, holdId, complete: true, ct), ct);

    /// <summary>Delivery failed: Holding pays the buyer back. Not blocked by freezes.</summary>
    public Task<MoneyResult> ReturnHoldAsync(Guid txnId, long holdId, CancellationToken ct) =>
        Idempotent(txnId, "hold_return", holdId, (conn, tx) => SettleAsync(conn, tx, holdId, complete: false, ct), ct);

    /// <summary>Returns every open hold older than <see cref="HoldLifetime"/>; how many were returned.</summary>
    public async Task<int> ReturnExpiredHoldsAsync(CancellationToken ct)
    {
        List<long> due = [];
        try
        {
            await using var conn = await open(ct);
            await using var cmd = new MySqlCommand("SELECT id FROM holds WHERE state = 'open' AND created <= @cutoff ORDER BY id", conn);
            cmd.Parameters.AddWithValue("@cutoff", Now - HoldLifetime);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) due.Add(r.GetInt64(0));
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }

        var returned = 0;
        foreach (var id in due)
            if ((await ReturnHoldAsync(Guid.NewGuid(), id, ct)).Outcome == MoneyOutcome.Ok)
                returned++;
        return returned;
    }

    /// <summary>The region a hold was made in, or null for an unknown hold (or one made without a region).</summary>
    public async Task<Guid?> HoldRegionAsync(long holdId, CancellationToken ct)
    {
        try
        {
            await using var conn = await open(ct);
            await using var cmd = new MySqlCommand("SELECT region_id FROM holds WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@id", holdId);
            // MySqlConnector reads CHAR(36) as a Guid.
            return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    private async Task<MoneyResult> SettleAsync(MySqlConnection conn, MySqlTransaction tx, long holdId, bool complete, CancellationToken ct)
    {
        var hold = await LockHoldAsync(conn, tx, holdId, ct);
        if (hold is null) return MoneyResult.Of(MoneyOutcome.UnknownHold);
        if (hold.State != "open") return MoneyResult.Of(MoneyOutcome.HoldClosed);

        var entry = complete
            ? new Entry(EntryType.HoldCompleted, hold.RegionId, hold.ObjectId, hold.ObjectName,
                Reason: hold.Kind == "land" ? EntryType.LandSale : EntryType.ObjectSale, HoldId: hold.Id)
            : new Entry(EntryType.HoldReturned, hold.RegionId, hold.ObjectId, hold.ObjectName, HoldId: hold.Id);
        var moved = await MoveBetweenAsync(conn, tx, MoneySchema.HoldingId, complete ? hold.SellerId : hold.BuyerId, hold.Amount, entry, guarded: false, ct);
        await ExecAsync(conn, tx, "UPDATE holds SET state = @state, settled = @now WHERE id = @id",
            ct, ("@state", complete ? "completed" : "returned"), ("@now", Now), ("@id", hold.Id));
        return moved with { HoldId = hold.Id };
    }

    private static async Task<HoldRow?> LockHoldAsync(MySqlConnection conn, MySqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "SELECT id, buyer_id, seller_id, amount, kind, region_id, object_id, object_name, state FROM holds WHERE id = @id FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new HoldRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetGuid(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8));
    }
}
