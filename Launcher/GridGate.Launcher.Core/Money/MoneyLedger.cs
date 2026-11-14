using System.Data;
using System.Text.Json;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// The coin ledger (money design §7.1, §7.2): every rule and every SQL statement about coins. Each movement is one database
/// transaction that changes two balances and writes one entry; only the Grid bank may go below zero; a repeated transaction
/// id returns the first answer. Any database failure reads as <see cref="MoneyOutcome.Unavailable"/>, never an exception.
/// </summary>
public sealed partial class MoneyLedger(
    Func<CancellationToken, Task<MySqlConnection>> open,
    Func<MoneySettings> settings,
    Func<MoneyMode> effectiveMode,
    Func<Guid, CancellationToken, Task<bool>> isLocalUser,
    TimeProvider time)
{
    private const int DeadlockRetries = 3;
    private const int ErDuplicateKey = 1062, ErDeadlock = 1213, ErLockWaitTimeout = 1205;

    private sealed record Account(long Id, AccountKind Kind, long Balance, bool Frozen);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>The balance of <paramref name="who"/> in <see cref="MoneyResult.FromBalance"/>; creates the account if missing.</summary>
    public Task<MoneyResult> BalanceAsync(AccountRef who, CancellationToken ct) =>
        RunAsync(async (conn, tx) =>
        {
            var (account, refusal) = await ResolveAsync(conn, tx, who, ct);
            if (account is null) return MoneyResult.Of(refusal);
            account = await LockAsync(conn, tx, account.Id, ct);
            account = await ApplyAllowanceAsync(conn, tx, account, ct);
            return new MoneyResult(MoneyOutcome.Ok, account.Balance);
        }, ct);

    /// <summary>One avatar paying another (or an object's owner).</summary>
    public Task<MoneyResult> TransferAsync(TransferRequest r, CancellationToken ct)
    {
        if (!AmountOk(r.Amount)) return Task.FromResult(MoneyResult.Of(MoneyOutcome.BadAmount));
        if (Same(r.From, r.To)) return Task.FromResult(MoneyResult.Of(MoneyOutcome.NotAllowed));
        return Idempotent(r.TxnId, "transfer", (conn, tx) =>
            MoveAsync(conn, tx, r.From, r.To, r.Amount, new Entry(r.Type, r.RegionId, r.ObjectId, r.ObjectName), ct), ct);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The moving parts the other operations share.

    /// <summary>What an entry records besides the two accounts and the amount.</summary>
    private sealed record Entry(
        string Type,
        Guid? RegionId = null,
        Guid? ObjectId = null,
        string? ObjectName = null,
        string? Staff = null,
        string? Reason = null,
        string? PaymentRef = null,
        long? Reverses = null,
        long? HoldId = null);

    private bool AmountOk(long amount) => amount > 0 && amount <= settings().MaxPayment;

    private static bool Same(AccountRef a, AccountRef b) => a.AgentId == b.AgentId && NormalHome(a.Home) == NormalHome(b.Home);

    /// <summary>A visitor's home as stored: trimmed, without a trailing slash, lower case.</summary>
    private static string NormalHome(string home) => home.Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>Resolves both accounts and moves the coins from <paramref name="from"/> to <paramref name="to"/>, guarded.</summary>
    private async Task<MoneyResult> MoveAsync(MySqlConnection conn, MySqlTransaction tx, AccountRef from, AccountRef to, long amount, Entry entry, CancellationToken ct)
    {
        if (await GridFrozenAsync(conn, tx, ct)) return MoneyResult.Of(MoneyOutcome.Frozen);
        var (payer, r1) = await ResolveAsync(conn, tx, from, ct);
        if (payer is null) return MoneyResult.Of(r1);
        var (payee, r2) = await ResolveAsync(conn, tx, to, ct);
        if (payee is null) return MoneyResult.Of(r2);
        return await MoveBetweenAsync(conn, tx, payer.Id, payee.Id, amount, entry, guarded: true, ct);
    }

    /// <summary>
    /// Locks both accounts in id order, applies any allowance due, then debits (guarded unless <paramref name="guarded"/> is
    /// false: settling a hold), credits and writes the entry.
    /// </summary>
    private async Task<MoneyResult> MoveBetweenAsync(MySqlConnection conn, MySqlTransaction tx, long fromId, long toId, long amount, Entry entry, bool guarded, CancellationToken ct)
    {
        var first = await LockAsync(conn, tx, Math.Min(fromId, toId), ct);
        var second = await LockAsync(conn, tx, Math.Max(fromId, toId), ct);
        var payer = await ApplyAllowanceAsync(conn, tx, first.Id == fromId ? first : second, ct);
        var payee = await ApplyAllowanceAsync(conn, tx, first.Id == fromId ? second : first, ct);

        if (guarded)
        {
            if (payer.Frozen) return MoneyResult.Of(MoneyOutcome.AccountFrozen);
            var debited = await ExecAsync(conn, tx,
                "UPDATE accounts SET balance = balance - @amt WHERE id = @id AND frozen = 0 AND (kind = 'Bank' OR balance >= @amt)",
                ct, ("@amt", amount), ("@id", payer.Id));
            if (debited != 1) return MoneyResult.Of(MoneyOutcome.NotEnough);
        }
        else
            await ExecAsync(conn, tx, "UPDATE accounts SET balance = balance - @amt WHERE id = @id", ct, ("@amt", amount), ("@id", payer.Id));
        await ExecAsync(conn, tx, "UPDATE accounts SET balance = balance + @amt WHERE id = @id", ct, ("@amt", amount), ("@id", payee.Id));

        var fromAfter = payer.Balance - amount;
        var toAfter = payee.Balance + amount;
        var entryId = await WriteEntryAsync(conn, tx, payer.Id, payee.Id, amount, fromAfter, toAfter, entry, ct);
        return new MoneyResult(MoneyOutcome.Ok, fromAfter, toAfter, entryId);
    }

    private async Task<long> WriteEntryAsync(MySqlConnection conn, MySqlTransaction tx, long fromId, long toId, long amount, long fromAfter, long toAfter, Entry e, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO entries (at, type, from_id, to_id, amount, from_after, to_after, region_id, object_id, object_name, staff, reason, payment_ref, reverses, hold_id)
            VALUES (@at, @type, @from, @to, @amt, @fa, @ta, @region, @object, @name, @staff, @reason, @ref, @rev, @hold)
            """, conn, tx);
        cmd.Parameters.AddWithValue("@at", Now);
        cmd.Parameters.AddWithValue("@type", e.Type);
        cmd.Parameters.AddWithValue("@from", fromId);
        cmd.Parameters.AddWithValue("@to", toId);
        cmd.Parameters.AddWithValue("@amt", amount);
        cmd.Parameters.AddWithValue("@fa", fromAfter);
        cmd.Parameters.AddWithValue("@ta", toAfter);
        cmd.Parameters.AddWithValue("@region", e.RegionId?.ToString());
        cmd.Parameters.AddWithValue("@object", e.ObjectId?.ToString());
        cmd.Parameters.AddWithValue("@name", Trim(e.ObjectName, 255));
        cmd.Parameters.AddWithValue("@staff", Trim(e.Staff, 128));
        cmd.Parameters.AddWithValue("@reason", Trim(e.Reason, 500));
        cmd.Parameters.AddWithValue("@ref", Trim(e.PaymentRef, 128));
        cmd.Parameters.AddWithValue("@rev", e.Reverses);
        cmd.Parameters.AddWithValue("@hold", e.HoldId);
        await cmd.ExecuteNonQueryAsync(ct);
        return cmd.LastInsertedId;
    }

    /// <summary>
    /// The account for <paramref name="who"/>, creating it when allowed: a resident only if they are a local user (§7.2), a
    /// visitor only while visitor wallets are on. System accounts can't be named. Doesn't lock.
    /// </summary>
    private async Task<(Account? Account, MoneyOutcome Refusal)> ResolveAsync(MySqlConnection conn, MySqlTransaction tx, AccountRef who, CancellationToken ct)
    {
        var home = NormalHome(who.Home);
        if (who.AgentId == Guid.Empty || home.StartsWith("system:", StringComparison.Ordinal) || home.Length > 255)
            return (null, MoneyOutcome.NoWallet);

        if (await FindAsync(conn, tx, who.AgentId, home, ct) is { } found)
            return (found, MoneyOutcome.Ok);

        var resident = home.Length == 0;
        if (resident ? !await isLocalUser(who.AgentId, ct) : !settings().VisitorWallets)
            return (null, MoneyOutcome.NoWallet);

        var created = await ExecAsync(conn, tx,
            "INSERT IGNORE INTO accounts (agent_id, home, kind, created) VALUES (@agent, @home, @kind, @now)",
            ct, ("@agent", who.AgentId.ToString()), ("@home", home), ("@kind", (resident ? AccountKind.Person : AccountKind.Visitor).ToString()), ("@now", Now));
        var account = await FindAsync(conn, tx, who.AgentId, home, ct)
            ?? throw new InvalidOperationException("An account just created can't be read back.");

        // Only the transaction that created the account gives the starting balance.
        var start = resident ? settings().StartingBalance(effectiveMode()) : 0;
        if (created == 1 && start > 0)
            await MoveBetweenAsync(conn, tx, MoneySchema.BankId, account.Id, start, new Entry(EntryType.StartingBalance), guarded: true, ct);
        return (account, MoneyOutcome.Ok);
    }

    private static async Task<Account?> FindAsync(MySqlConnection conn, MySqlTransaction tx, Guid agent, string home, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT id, kind, balance, frozen FROM accounts WHERE agent_id = @agent AND home = @home", conn, tx);
        cmd.Parameters.AddWithValue("@agent", agent.ToString());
        cmd.Parameters.AddWithValue("@home", home);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Locks one account row and reads it fresh.</summary>
    private static async Task<Account> LockAsync(MySqlConnection conn, MySqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT id, kind, balance, frozen FROM accounts WHERE id = @id FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"Account {id} is missing.");
        return Read(r);
    }

    private static Account Read(MySqlDataReader r) =>
        new(r.GetInt64(0), Enum.Parse<AccountKind>(r.GetString(1)), r.GetInt64(2), r.GetBoolean(3));

    /// <summary>The weekly allowance, applied when a resident's (locked) account is touched and a week has passed.</summary>
    private async Task<Account> ApplyAllowanceAsync(MySqlConnection conn, MySqlTransaction tx, Account a, CancellationToken ct)
    {
        var weekly = settings().WeeklyAllowance;
        if (weekly <= 0 || a.Kind != AccountKind.Person) return a;

        await using (var cmd = new MySqlCommand("SELECT last_allowance FROM accounts WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("@id", a.Id);
            var last = await cmd.ExecuteScalarAsync(ct);
            if (last is DateTime when && Now - when < TimeSpan.FromDays(7)) return a;
        }

        await ExecAsync(conn, tx, "UPDATE accounts SET balance = balance - @amt WHERE id = @bank", ct, ("@amt", weekly), ("@bank", MoneySchema.BankId));
        await ExecAsync(conn, tx, "UPDATE accounts SET balance = balance + @amt, last_allowance = @now WHERE id = @id", ct, ("@amt", weekly), ("@now", Now), ("@id", a.Id));
        var bankAfter = await ScalarAsync(conn, tx, "SELECT balance FROM accounts WHERE id = @bank", ct, ("@bank", MoneySchema.BankId));
        await WriteEntryAsync(conn, tx, MoneySchema.BankId, a.Id, weekly, bankAfter, a.Balance + weekly, new Entry(EntryType.Allowance), ct);
        return a with { Balance = a.Balance + weekly };
    }

    private static async Task<bool> GridFrozenAsync(MySqlConnection conn, MySqlTransaction tx, CancellationToken ct) =>
        await ScalarAsync(conn, tx, "SELECT CAST(value AS SIGNED) FROM state WHERE name = 'frozen'", ct) != 0;

    // ---------------------------------------------------------------------------------------------------------------
    // Transactions, retries and idempotency.

    /// <summary>
    /// Runs <paramref name="work"/> once per <paramref name="txnId"/>: an id already answered returns that answer and
    /// changes nothing. Only Ok answers are kept: a refusal changed nothing, so asking again is harmless.
    /// </summary>
    private Task<MoneyResult> Idempotent(Guid txnId, string op, Func<MySqlConnection, MySqlTransaction, Task<MoneyResult>> work, CancellationToken ct) =>
        RunAsync(async (conn, tx) =>
        {
            if (await StoredAsync(conn, tx, txnId, ct) is { } earlier) return earlier;
            var result = await work(conn, tx);
            if (result.Outcome == MoneyOutcome.Ok)
                await ExecAsync(conn, tx, "INSERT INTO requests (txn_id, op, response, at) VALUES (@txn, @op, @resp, @now)",
                    ct, ("@txn", txnId.ToString()), ("@op", op), ("@resp", JsonSerializer.Serialize(result)), ("@now", Now));
            return result;
        }, ct, txnId);

    private static async Task<MoneyResult?> StoredAsync(MySqlConnection conn, MySqlTransaction? tx, Guid txnId, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT response FROM requests WHERE txn_id = @txn", conn, tx);
        cmd.Parameters.AddWithValue("@txn", txnId.ToString());
        return await cmd.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<MoneyResult>(json) : null;
    }

    /// <summary>
    /// One database transaction: committed when <paramref name="work"/> answers Ok, rolled back otherwise. Deadlocks are
    /// retried; a duplicate transaction id (two copies racing) returns the winner's answer; any other database failure is
    /// <see cref="MoneyOutcome.Unavailable"/>.
    /// </summary>
    private async Task<MoneyResult> RunAsync(Func<MySqlConnection, MySqlTransaction, Task<MoneyResult>> work, CancellationToken ct, Guid? txnId = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var conn = await open(ct);
                await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                var result = await work(conn, tx);
                if (result.Outcome == MoneyOutcome.Ok)
                    await tx.CommitAsync(ct);
                else
                    await tx.RollbackAsync(ct);
                return result;
            }
            catch (MySqlException e) when ((e.Number == ErDeadlock || e.Number == ErLockWaitTimeout) && attempt < DeadlockRetries)
            {
                await Task.Delay(Random.Shared.Next(5, 30), ct);
            }
            catch (MySqlException e) when (e.Number == ErDuplicateKey && txnId is { } id)
            {
                return await ReadStoredAsync(id, ct) ?? MoneyResult.Unavailable;
            }
            catch (Exception e) when (e is MySqlException or TimeoutException or InvalidOperationException { Source: "MySqlConnector" })
            {
                return MoneyResult.Unavailable;
            }
        }
    }

    private async Task<MoneyResult?> ReadStoredAsync(Guid txnId, CancellationToken ct)
    {
        try
        {
            await using var conn = await open(ct);
            return await StoredAsync(conn, null, txnId, ct);
        }
        catch (MySqlException)
        {
            return null;
        }
    }

    private static async Task<int> ExecAsync(MySqlConnection conn, MySqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarAsync(MySqlConnection conn, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    /// <summary>Cuts <paramref name="s"/> to <paramref name="max"/> UTF-16 units without splitting a surrogate pair.</summary>
    private static string? Trim(string? s, int max)
    {
        if (s is null || s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }
}
