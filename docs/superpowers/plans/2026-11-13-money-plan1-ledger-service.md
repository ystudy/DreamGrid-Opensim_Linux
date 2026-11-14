# Money plan 1: ledger and money service — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build GridGate's coin ledger and the region-facing money service inside the launcher, fully tested, with every money
mode still hidden behind the release constants.

**Architecture:** A new `money` MariaDB database holds accounts, entries, holds and request records. `MoneyLedger`
(Launcher.Core) owns every rule and every SQL statement. A separate in-process Kestrel host (`MoneyServiceHost`) on its own
HTTPS port exposes the ledger to region programs. Each call is authenticated by a per-program region key and checked against
Robust's `Presence` table. A background service runs the self-check and returns expired holds.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs (second `WebApplication`), MySqlConnector 2.6.2, xunit.v3 on
Microsoft.Testing.Platform, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-11-13-money-design.md` (approved 2026-11-13). This plan covers spec §7.1, §7.2, §7.4,
§7.5, the service side of §7.6, and the tests in §12. It is step 1 of spec §14.

**Later plans (written when reached, from the code this one produces):**
- Plan 2: region module, OpenSim changes, economy helper, currency name, Money and My coins pages.
- Plan 3: hypergrid visitors on the module side, Region Server pairing, LAN listening.
- Plan 4: Gloebit.
- Plan 5: the AI review gate.
- Plan 6: real coins.

## Global Constraints

- Coins are whole numbers (`long`). Every amount is `> 0` and `<= MoneySettings.MaxPayment`, default **1,000,000**.
- **A resident account is only ever created for a real local user.** The UUID must be in Robust's `UserAccounts.PrincipalID`;
  anything else gets `NoWallet`. Otherwise a taken-over region could mint starting balances by inventing UUIDs.
- Only the Grid bank account may go below zero. Every balance change has exactly one matching entry, in the same database
  transaction.
- Defaults, from spec §7.5:
  - `CurrencySymbol` "C$", `CurrencyName` "coins";
  - `StartingBalancePlay` 100, `StartingBalanceReal` 0;
  - `WeeklyAllowance` 0;
  - `UploadFee` 0, `ClassifiedFee` 0;
  - `ScriptPayouts` true, `DailyPayoutLimit` 5,000, `VisitorWallets` true.
- Hold expiry: **5 minutes**. Completing, returning or expiring an existing hold is never blocked by a freeze.
- The money service is HTTPS only and listens on `127.0.0.1` only in this plan. Errors are fixed codes. Keys, session ids,
  passwords and connection strings never appear in logs or responses.
- `MoneyRelease.PlayCoinsApproved` and `MoneyRelease.RealCoinsApproved` are `false`. While false, the effective mode is
  `Off` whatever the settings file says, and the money service doesn't start.
- Windows and Debian behave the same. `TreatWarningsAsErrors` is on in every project.
- Tests that need MariaDB use `IntegrationEnvironment.RequireMariaDbBin()`. They skip without it, and fail when
  `GRIDGATE_REQUIRE_INTEGRATION=1`.
- Work in the worktree `C:\Users\ystud\Desktop\CODE\GridGate-currency-design`. Before each commit, check that
  `git branch --show-current` names the plan's branch.
- Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **The launcher restarts while a hold is open.** The hold must still return to the buyer after 5 minutes, because expiry
   reads the hold's stored time, not memory. Test: Task 4, `Hold_survives_a_new_ledger_instance_and_expires`.
2. **MariaDB is down or refuses.** Every ledger call answers `Unavailable`, never an exception or a 500 with details. Tests:
   Task 3, `Unreachable_database_reads_as_unavailable`; Task 9, `Database_down_returns_unavailable_code`.
3. **Payments cross in both directions at once** (A→B and B→A). The lock order prevents a deadlock, or a retry absorbs it;
   all succeed and the totals add up. Test: Task 3, `Crossing_transfers_all_complete`.
4. **Very long or unusual object names and reasons** (300 characters, emoji). These are trimmed to the column size, never an
   error. Test: Task 3, `Long_object_name_is_trimmed`.
5. **The daily payout limit across midnight.** It is a rolling 24 hours, so payouts at 23:59 and 00:01 count together. Test:
   Task 5, `Payout_limit_is_rolling_24_hours`.

---

### Task 1: Money settings and the release gate

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneySettings.cs`
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyRelease.cs`
- Modify: `Launcher/GridGate.Launcher.Core/Settings/LauncherSettings.cs`. `DatabaseSecrets` (line 6) gains
  `string MoneyPassword = ""`. `CurrentSchema` becomes 7. `LauncherSettings` gains `MoneyPort` and `Money`.
- Modify: `Launcher/GridGate.Launcher.Core/Settings/SettingsStore.cs:89` (the comment covers 6 to 7: money arrives with its
  defaults).
- Modify: `Launcher/GridGate.Launcher.Core/Grid/PortAllocator.cs:19`. Add `s.MoneyPort` to `Taken`.
- Test: `Launcher/GridGate.Launcher.Tests/MoneySettingsTests.cs`

**Interfaces:**
- Produces:
  - `enum MoneyMode { Off, Play, Real, Gloebit }`, saved by name with `JsonStringEnumConverter<MoneyMode>`.
  - `sealed record MoneySettings(MoneyMode Mode = Off, string CurrencySymbol = "C$", string CurrencyName = "coins", long StartingBalancePlay = 100, long StartingBalanceReal = 0, long WeeklyAllowance = 0, long UploadFee = 0, long ClassifiedFee = 0, bool ScriptPayouts = true, long DailyPayoutLimit = 5000, bool VisitorWallets = true, long MaxPayment = 1_000_000, Dictionary<string,string>? ProgramKeys = null)`.
    `ProgramKeys` maps a program instance to its region key; null reads as empty.
  - `LauncherSettings.Money`, with `[AllowNull]` and the same null-reads-as-default pattern as `Iar`.
  - `LauncherSettings.MoneyPort`, default **8000**.
  - `static class MoneyRelease`:
    - `const bool PlayCoinsApproved = false;` and `const bool RealCoinsApproved = false;`
    - `static MoneyMode Effective(MoneySettings m)`. It returns `Off` unless the mode is approved: Play and Gloebit need
      `PlayCoinsApproved`, Real needs both.
  - `MoneySettings.StartingBalance(MoneyMode effective)` returns `StartingBalancePlay` for Play, `StartingBalanceReal` for
    Real, and 0 otherwise.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Defaults_match_the_spec()
{
    var m = new MoneySettings();
    Assert.Equal(MoneyMode.Off, m.Mode); Assert.Equal("C$", m.CurrencySymbol); Assert.Equal("coins", m.CurrencyName);
    Assert.Equal(100, m.StartingBalancePlay); Assert.Equal(0, m.StartingBalanceReal); Assert.Equal(5000, m.DailyPayoutLimit);
    Assert.Equal(1_000_000, m.MaxPayment); Assert.True(m.ScriptPayouts); Assert.True(m.VisitorWallets);
}
[Theory, InlineData(MoneyMode.Play), InlineData(MoneyMode.Real), InlineData(MoneyMode.Gloebit)]
public void Unapproved_modes_read_as_off(MoneyMode mode) =>
    Assert.Equal(MoneyMode.Off, MoneyRelease.Effective(new MoneySettings(Mode: mode)));
[Fact] public void Schema_6_file_without_money_loads_with_defaults() // write a schema-6 JSON with Db{3 passwords}, load via SettingsStore
{ /* Assert.Equal(7, s.SchemaVersion); Assert.Equal("", s.Db.MoneyPassword); Assert.Equal(MoneyMode.Off, s.Money.Mode); Assert.Equal(8000, s.MoneyPort); */ }
[Fact] public void Money_port_is_taken() => Assert.Contains(8000, PortAllocator.Taken(TestSettings.Minimal()));
```

`TestSettings.Minimal()` stands for however the existing tests build a minimal `LauncherSettings`. Reuse that helper; check
`GridConfigWriterTests` for it.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class GridGate.Launcher.Tests.MoneySettingsTests`
Expected: build fails, because `MoneySettings` isn't defined yet.

- [ ] **Step 3: Implement the records, the gate, the schema bump and the port**

- [ ] **Step 4: Run the class, then the whole unit suite**

Run the class: expect PASS.
Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`
Expected: everything passes. Snapshot tests are unaffected, because nothing writes INI keys yet.

- [ ] **Step 5: Commit** — `feat(money): settings, release gate, money port`

---

### Task 2: The money database and its schema

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Database/DatabaseProvisioner.cs:15`. When `s.Db.MoneyPassword != ""`, also
  `Provision(conn, "money", "moneyuser", s.Db.MoneyPassword, ct)`.
- Modify: `Launcher/GridGate.Launcher.Core/Database/GridDb.cs`. Add `OpenMoneyAsync(LauncherSettings s, uint timeoutSeconds, CancellationToken ct)`.
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneySchema.cs`
- Create: `Launcher/GridGate.Launcher.IntegrationTests/MoneyTestDatabase.cs`. It starts its own MariaDB the way
  `RegionStoreTests.StartAsync` does, creates `money` and a `robust` database holding only `Presence` (columns copied from
  `OpenSim/Data/MySQL/Resources/Presence.migrations`) and `UserAccounts` (only `PrincipalID char(36)`, `FirstName`, `LastName`), and exposes `LauncherSettings Settings` and `Task Sql(string)`.
- Modify: `Launcher/GridGate.Launcher.IntegrationTests/GridGate.Launcher.IntegrationTests.csproj`. Add
  `Microsoft.Extensions.TimeProvider.Testing` 10.0.0.
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneySchemaTests.cs`

**Interfaces:**
- Produces:
  - `static class MoneySchema { const int Version = 1; static Task EnsureAsync(MySqlConnection money, CancellationToken ct); }`
    It is safe to run repeatedly, and it creates the three system accounts once.
  - `enum AccountKind { Person, Visitor, Bank, Fees, Holding }`
  - System account ids are fixed: Bank = 1, Fees = 2, Holding = 3.

The DDL. The spec pins these columns; write it exactly. All tables are InnoDB, utf8mb4.

```sql
CREATE TABLE IF NOT EXISTS accounts (
  id BIGINT AUTO_INCREMENT PRIMARY KEY, agent_id CHAR(36) NOT NULL, home VARCHAR(255) NOT NULL DEFAULT '',
  kind VARCHAR(16) NOT NULL, balance BIGINT NOT NULL DEFAULT 0, frozen TINYINT NOT NULL DEFAULT 0,
  payouts_allowed TINYINT NOT NULL DEFAULT 0, last_allowance DATETIME(6) NULL, created DATETIME(6) NOT NULL,
  UNIQUE KEY agent_home (agent_id, home));
CREATE TABLE IF NOT EXISTS entries (
  id BIGINT AUTO_INCREMENT PRIMARY KEY, at DATETIME(6) NOT NULL, type VARCHAR(32) NOT NULL,
  from_id BIGINT NOT NULL, to_id BIGINT NOT NULL, amount BIGINT NOT NULL, from_after BIGINT NOT NULL, to_after BIGINT NOT NULL,
  region_id CHAR(36) NULL, object_id CHAR(36) NULL, object_name VARCHAR(255) NULL, staff VARCHAR(128) NULL,
  reason VARCHAR(500) NULL, payment_ref VARCHAR(128) NULL, reverses BIGINT NULL, hold_id BIGINT NULL,
  UNIQUE KEY payment_ref (payment_ref), KEY from_at (from_id, at), KEY to_at (to_id, at), KEY type_from_at (type, from_id, at));
CREATE TABLE IF NOT EXISTS holds (
  id BIGINT AUTO_INCREMENT PRIMARY KEY, buyer_id BIGINT NOT NULL, seller_id BIGINT NOT NULL, amount BIGINT NOT NULL,
  kind VARCHAR(16) NOT NULL, region_id CHAR(36) NULL, object_id CHAR(36) NULL, object_name VARCHAR(255) NULL,
  created DATETIME(6) NOT NULL, state VARCHAR(16) NOT NULL, settled DATETIME(6) NULL, KEY state_created (state, created));
CREATE TABLE IF NOT EXISTS requests (
  txn_id CHAR(36) PRIMARY KEY, op VARCHAR(32) NOT NULL, response TEXT NOT NULL, at DATETIME(6) NOT NULL);
CREATE TABLE IF NOT EXISTS state (name VARCHAR(64) PRIMARY KEY, value VARCHAR(500) NOT NULL);
```

System accounts:
- `agent_id` is all zeros and `home` is `system:bank`, `system:fees` or `system:holding`. Insert them with explicit ids
  1, 2 and 3, using `INSERT IGNORE`.
- `state` gets `schema_version=1`, `frozen=0` and `frozen_reason=''`, also with `INSERT IGNORE`.

- [ ] **Step 1: Write the failing integration tests:** `Ensure_twice_is_harmless`, which checks that there are exactly 3
  accounts and that `schema_version` is "1"; and `Provisioner_creates_money_only_with_a_password`, where an empty
  `MoneyPassword` gives no `money` database.
- [ ] **Step 2: Run and see them fail.** Run with `GRIDGATE_MARIADB_BIN` set:
  `dotnet test --project Launcher/GridGate.Launcher.IntegrationTests -c Release -- --filter-class GridGate.Launcher.IntegrationTests.MoneySchemaTests`.
  Expected: build fails, because `MoneySchema` isn't defined yet.
- [ ] **Step 3: Implement the schema, the provisioner change, `OpenMoneyAsync` and `MoneyTestDatabase`**
- [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Commit** — `feat(money): money database and schema`

---

### Task 3: Ledger core — accounts and transfers

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyTypes.cs`
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyLedger.cs`. This task adds the core; Tasks 4 to 6 extend it.
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneyLedgerTransferTests.cs`

**Interfaces:**
- Produces, in `MoneyTypes.cs`:
  - `readonly record struct AccountRef(Guid AgentId, string Home)`. An empty `Home` means a resident of this grid.
    `static AccountRef Resident(Guid id)`.
  - `enum MoneyOutcome { Ok, NotEnough, Frozen, AccountFrozen, Unavailable, BadAmount, NotAllowed, OverDailyLimit, UnknownHold, HoldClosed, NoWallet, DuplicateReference }`
  - `sealed record MoneyResult(MoneyOutcome Outcome, long? FromBalance = null, long? ToBalance = null, long? EntryId = null, long? HoldId = null)`
  - `static class EntryType` holds string constants, the spec §7.4 list in snake case: `starting_balance`, `allowance`,
    `staff_gift`, `staff_take`, `bought_coins`, `pay_avatar`, `pay_object`, `object_sale`, `land_sale`, `parcel_pass`,
    `script_payout`, `upload_fee`, `group_join_fee`, `classified_fee`, `hold`, `hold_completed`, `hold_returned`, `refund`,
    `wallet_merge`, `fee_moved`.
  - `sealed record TransferRequest(Guid TxnId, string Type, AccountRef From, AccountRef To, long Amount, Guid? RegionId = null, Guid? ObjectId = null, string? ObjectName = null)`
- Produces, in `MoneyLedger.cs`:
  - Constructor: `public MoneyLedger(Func<CancellationToken, Task<MySqlConnection>> open, Func<MoneySettings> settings, Func<MoneyMode> effectiveMode, Func<Guid, CancellationToken, Task<bool>> isLocalUser, TimeProvider time)`.
    `isLocalUser` is Task 7's `PresenceCheck.IsLocalUserAsync`; tests pass a lambda over a set.
  - `Task<MoneyResult> BalanceAsync(AccountRef who, CancellationToken ct)`. It creates the account if it's missing (§ rules
    below) and returns the balance in `FromBalance`.
  - `Task<MoneyResult> TransferAsync(TransferRequest r, CancellationToken ct)`

**Rules this task implements:**

- **Account creation** (`GetOrCreateAsync`, private):
  - A resident (`Home == ""`) is created with kind Person only when `isLocalUser(id)` is true; otherwise `NoWallet`, and nothing
    is written. If `StartingBalance(effectiveMode()) > 0`, a `starting_balance`
    entry from Bank is written in the same transaction.
  - A visitor is created with kind Visitor and balance 0, but only when `VisitorWallets` is true. Otherwise the call answers
    `NoWallet`.
- **Weekly allowance:** applied lazily on account touch. When `WeeklyAllowance > 0` and `last_allowance` is null or more
  than 7 days ago, the account gets an `allowance` entry from Bank and `last_allowance = now`. Residents only.
- **Amount check:** `Amount <= 0` or `Amount > MaxPayment` gives `BadAmount`, and nothing is written.
- **Global freeze:** `state.frozen = 1` gives `Frozen` for transfers. Holds settle anyway (Task 4).
- **Idempotency:** in the same transaction, `INSERT INTO requests (txn_id, op, response, at)`. On a duplicate key, roll
  back, then read and return the stored `response` (the `MoneyResult` as JSON), changing nothing.
- **Debit and credit**, in one transaction. Lock both account rows `SELECT … FOR UPDATE` in ascending id order. Then:

  ```sql
  UPDATE accounts SET balance = balance - @amt WHERE id = @from AND frozen = 0 AND (kind = 'Bank' OR balance >= @amt);
  -- 0 rows: AccountFrozen if from.frozen, else NotEnough → rollback
  UPDATE accounts SET balance = balance + @amt WHERE id = @to;
  INSERT INTO entries (...) VALUES (... from_after, to_after read back after the updates ...);
  ```

- **Deadlocks:** retry MySQL error 1213 up to 3 times.
- **Unavailable:** any `MySqlException` or connection failure gives `Unavailable`. Log only the exception type and number.
- **Trimming:** `object_name` is trimmed to 255 characters and `reason` to 500, at a character boundary (don't split a
  surrogate pair).

- [ ] **Step 1: Write the failing tests** on `MoneyTestDatabase`, with effective mode Play:

```csharp
[Fact] Task New_resident_gets_starting_balance()      // Balance(A).FromBalance == 100; one starting_balance entry from 1 to A
[Fact] Task Unknown_resident_id_gets_NoWallet()       // isLocalUser false → NoWallet as payer or payee; no account row, no entry
[Fact] Task Visitor_starts_at_zero()                  // Balance(new AccountRef(V, "http://other:8002")).FromBalance == 0
[Fact] Task Visitor_without_wallets_gets_NoWallet()   // VisitorWallets=false → NoWallet
[Fact] Task Transfer_moves_coins_and_writes_one_entry() // A→B 30: A 70, B 130, entries row with from_after 70, to_after 130
[Fact] Task Transfer_more_than_balance_is_NotEnough()   // A→B 101 → NotEnough, balances unchanged, no entry
[Theory, InlineData(0), InlineData(-5), InlineData(1_000_001)] Task Bad_amounts_change_nothing(long amt) // BadAmount
[Fact] Task Frozen_grid_refuses_transfers()           // state.frozen=1 → Frozen
[Fact] Task Frozen_account_refuses_its_debits()       // A.frozen=1 → AccountFrozen
[Fact] Task Repeated_txn_pays_once()                  // same TxnId twice: second result equals first; A 70 not 40
[Fact] Task Parallel_transfers_never_overdraw()       // 50 × (A→B 3) at once from 100: Ok count == 33, A == 1, SUM(balance) == 0
[Fact] Task Crossing_transfers_all_complete()         // 20 A→B 1 and 20 B→A 1 in parallel: all Ok, A 100, B 100
[Fact] Task Only_the_bank_goes_negative()             // after starting balances, bank == -(sum of others); no other < 0
[Fact] Task Long_object_name_is_trimmed()             // 300 × "🎵" name stored as ≤255 chars, Outcome Ok
[Fact] Task Unreachable_database_reads_as_unavailable() // open() throws MySqlException → Unavailable, no throw
[Fact] Task Allowance_once_a_week()                   // WeeklyAllowance=10, FakeTimeProvider: touch → +10; +6 days → none; +8 days → +10
```

- [ ] **Step 2: Run them and see them fail**
- [ ] **Step 3: Implement `MoneyTypes.cs` and the `MoneyLedger` core**
- [ ] **Step 4: Run them and see them pass, three times in a row** (the concurrency tests must not flake)
- [ ] **Step 5: Commit** — `feat(money): ledger accounts and guarded transfers`

---

### Task 4: Holds for purchases

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneyLedger.cs`
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneyLedgerHoldTests.cs`

**Interfaces:**
- Consumes: Task 3's `TransferRequest`, `AccountRef` and `MoneyResult`.
- Produces:
  - `sealed record HoldRequest(Guid TxnId, string Kind /* "object" | "land" */, AccountRef Buyer, AccountRef Seller, long Amount, Guid? RegionId, Guid? ObjectId, string? ObjectName)`
  - `Task<MoneyResult> HoldAsync(HoldRequest r, CancellationToken ct)`. It moves the coins buyer → Holding (entry type
    `hold`), inserts a `holds` row with state `open`, and returns `HoldId`. It is refused by global and account freezes
    like a transfer.
  - `Task<MoneyResult> CompleteHoldAsync(Guid txnId, long holdId, CancellationToken ct)`. Holding → seller, entry type
    `hold_completed` with `hold_id`, entry type `object_sale` or `land_sale` recorded in `reason`, and state `completed`.
  - `Task<MoneyResult> ReturnHoldAsync(Guid txnId, long holdId, CancellationToken ct)`. Holding → buyer, entry type
    `hold_returned`, state `returned`.
  - `Task<int> ReturnExpiredHoldsAsync(CancellationToken ct)`. It returns every `open` hold created more than **5 minutes**
    ago and reports how many.
  - `public static readonly TimeSpan HoldLifetime = TimeSpan.FromMinutes(5);`
  - `Task<Guid?> HoldRegionAsync(long holdId, CancellationToken ct)`. Task 9 uses it to check that a complete or return
    comes from the region that made the hold.
- **Rules:**
  - Complete, return and expiry ignore `state.frozen` and `accounts.frozen`, and never use the balance guard on Holding,
    which always has the coins.
  - A hold that isn't `open` gives `HoldClosed`; an unknown id gives `UnknownHold`.
  - Each operation is idempotent by `txnId`, like transfers.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Task Complete_pays_the_seller()                 // hold 40: A 60, holding 40; complete: holding 0, B 140
[Fact] Task Return_gives_the_buyer_back()              // hold 40, return: A 100
[Fact] Task Settling_works_while_frozen()               // hold, then state.frozen=1 and A.frozen=1, B.frozen=1: complete Ok
[Fact] Task Second_settle_is_HoldClosed()               // complete then return → HoldClosed, balances unchanged
[Fact] Task Expired_holds_return_after_five_minutes()  // FakeTimeProvider +4:59 → 0 returned; +5:01 → 1 returned, A 100
[Fact] Task Hold_survives_a_new_ledger_instance_and_expires() // hold via ledger1; new MoneyLedger on same DB, +6 min → returned
[Fact] Task Hold_needs_enough_coins()                  // hold 101 → NotEnough
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Commit** — `feat(money): purchase holds that return on failure or expiry`

---

### Task 5: Script payouts and fees

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneyLedger.cs`
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneyLedgerPayoutTests.cs`

**Interfaces:**
- Produces:
  - `sealed record PayoutRequest(Guid TxnId, Guid OwnerId, AccountRef To, long Amount, Guid RegionId, Guid ObjectId, string ObjectName)`
  - `Task<MoneyResult> PayoutAsync(PayoutRequest r, CancellationToken ct)`. Entry type `script_payout`. The owner is always a
    resident (`AccountRef.Resident(OwnerId)`), because visitors' objects never pay out.
  - `Task<MoneyResult> SetPayoutsAllowedAsync(Guid residentId, bool allowed, CancellationToken ct)`
  - `sealed record ChargeRequest(Guid TxnId, string Type /* upload_fee | classified_fee | group_join_fee */, AccountRef Payer, long Amount, Guid? PayTo /* group founder, group_join_fee only */)`
  - `Task<MoneyResult> ChargeAsync(ChargeRequest r, CancellationToken ct)`. Upload and classified fees go to Fees. A group
    join fee goes to `AccountRef.Resident(PayTo)` when `PayTo` is a known resident account, and to Fees otherwise.
- **Payout rules, checked in this order:**
  1. `ScriptPayouts` false gives `NotAllowed`.
  2. The owner's `payouts_allowed` 0 gives `NotAllowed`.
  3. The sum of the owner's `script_payout` entries with `at > now - 24h`, plus `Amount`, over `DailyPayoutLimit` gives
     `OverDailyLimit`.
  4. Then the normal transfer path, guard included.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Task Payout_without_opt_in_is_refused()         // NotAllowed, no entry
[Fact] Task Payout_from_unknown_owner_is_NoWallet()     // owner not a local user → NoWallet, no account created
[Fact] Task Payout_after_opt_in_pays()                 // SetPayoutsAllowed(A,true); payout 10 → Ok, B 110
[Fact] Task Payout_switch_off_refuses_everyone()       // ScriptPayouts=false → NotAllowed even when opted in
[Fact] Task Payout_over_daily_limit_is_refused()       // DailyPayoutLimit=50: 30 Ok, 30 → OverDailyLimit
[Fact] Task Payout_limit_is_rolling_24_hours()          // Fake clock 23:59 pay 30; 00:01 next day pay 30 → OverDailyLimit; +24h01m → Ok
[Fact] Task Upload_fee_goes_to_fees()                  // Charge upload_fee 5: A 95, Fees +5
[Fact] Task Group_join_fee_goes_to_the_founder()       // PayTo=F (existing resident): F +20
[Fact] Task Group_join_fee_without_founder_goes_to_fees() // PayTo=null → Fees +20
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Commit** — `feat(money): opt-in script payouts with daily limit, and fees`

---

### Task 6: Staff operations and the self-check

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneyLedger.cs`
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyStaffTypes.cs`
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneyLedgerStaffTests.cs`

**Interfaces:**
- Produces:
  - `sealed record StaffAction(string StaffName, string Reason)`. A blank `Reason` gives `BadAmount` (no reason, no change).
    Reason is required (spec §9.1).
  - `Task<MoneyResult> GiveAsync(AccountRef to, long amount, StaffAction who, CancellationToken ct)`: Bank → account,
    entry type `staff_gift`.
  - `Task<MoneyResult> TakeAsync(AccountRef from, long amount, StaffAction who, CancellationToken ct)`: account → Bank,
    entry type `staff_take`, guarded.
  - `Task<MoneyResult> RefundAsync(long entryId, StaffAction who, CancellationToken ct)`. It reverses one entry: same amount,
    `to` → `from`, entry type `refund`, `reverses = entryId`, guarded. Refunding an entry that already has a refund gives
    `HoldClosed`.
  - `Task<MoneyResult> MoveFeesAsync(AccountRef to, long amount, StaffAction who, CancellationToken ct)`: entry type
    `fee_moved`.
  - `Task<MoneyResult> MergeWalletsAsync(AccountRef from, AccountRef to, StaffAction who, CancellationToken ct)`. It moves
    the whole balance with entry type `wallet_merge`, and freezes `from`.
  - `Task SetFrozenAsync(bool frozen, string reason, CancellationToken ct)` for the whole grid, and
    `Task SetAccountFrozenAsync(AccountRef who, bool frozen, CancellationToken ct)`.
  - `Task<(bool Frozen, string Reason)> FrozenAsync(CancellationToken ct)`
  - `sealed record MoneyTotals(long InExistence, long Residents, long Visitors, long Fees, long Holding, long MovedToday)` and
    `Task<MoneyTotals> TotalsAsync(CancellationToken ct)`. `InExistence` is the negated Bank balance.
  - `sealed record HistoryLine(long EntryId, DateTimeOffset At, string Type, AccountRef? Other, long Amount, long BalanceAfter, string? ObjectName, string? Reason)`
    and `Task<IReadOnlyList<HistoryLine>> HistoryAsync(AccountRef who, int max, CancellationToken ct)`, newest first.
  - `sealed record SelfCheckResult(bool Ok, long Sum, IReadOnlyList<(long AccountId, long Stored, long FromEntries)> Mismatches)`
    and `Task<SelfCheckResult> SelfCheckAsync(CancellationToken ct)`.
    - The check: the sum of all balances must be 0, and each account's balance must equal its credits minus its debits
      over `entries`.
    - When it isn't Ok, it calls `SetFrozenAsync(true, "Self-check found a difference", ct)` and returns the mismatches.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Task Give_and_take_need_a_reason()             // blank reason → BadAmount; with reason → Ok, entry.staff/reason set
[Fact] Task Take_cannot_go_below_zero()               // take 101 → NotEnough
[Fact] Task Refund_reverses_once()                    // transfer A→B 30, refund → A 100, B 100; refund again → HoldClosed
[Fact] Task Merge_moves_all_and_freezes_the_old()      // V1 has 25 → V2 +25, V1 0 and frozen
[Fact] Task Totals_add_up()                           // after a few movements: InExistence == Residents+Visitors+Fees+Holding
[Fact] Task Self_check_passes_on_clean_books()
[Fact] Task Self_check_catches_a_tampered_balance()  // Sql("UPDATE accounts SET balance=balance+5 WHERE id=A"): Ok false, A listed, grid frozen
[Fact] Task History_is_newest_first()
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Commit** — `feat(money): staff operations, totals, history and self-check`

---

### Task 7: Region keys and session checks

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/Money/RegionKeyRing.cs`
- Create: `Launcher/GridGate.Launcher.Core/Money/PresenceCheck.cs`
- Test: `Launcher/GridGate.Launcher.Tests/RegionKeyRingTests.cs`,
  `Launcher/GridGate.Launcher.IntegrationTests/PresenceCheckTests.cs`

**Interfaces:**
- Produces:
  - `sealed record RegionKeyGrant(string KeyId /* the program instance */, IReadOnlySet<Guid> Regions)`
  - `sealed class RegionKeyRing`:
    - `static RegionKeyRing FromSettings(LauncherSettings s)`. One grant per `s.Money.ProgramKeys` entry, covering
      `s.Regions.Where(r => r.Instance == instance && r.DeletedAt is null).Select(r => r.Uuid)`.
    - `bool TryAuthenticate(string? presented, out RegionKeyGrant? grant)`. It compares SHA-256 hashes with
      `CryptographicOperations.FixedTimeEquals`, and is false for null or empty input.
  - `static class MoneyKeys { static LauncherSettings EnsureProgramKeys(LauncherSettings s); }`. It adds
    `Secrets.NewPassword(48)` for every program without a key, and drops keys of programs that no longer exist. Plan 2
    calls this before writing INIs.
  - `sealed class PresenceCheck(Func<CancellationToken, Task<MySqlConnection>> openRobust)` with
    `Task<PresenceOutcome> CheckAsync(Guid agentId, Guid sessionId, Guid secureSessionId, IReadOnlySet<Guid> regions, CancellationToken ct)`,
    where `enum PresenceOutcome { Ok, NoSession, WrongRegion, Unavailable }`.
    - It runs one parameterized `SELECT RegionID FROM Presence WHERE UserID = @u AND SessionID = @s AND SecureSessionID = @ss`.
    - A zero `Guid` for either session id gives `NoSession` without querying.
    - `Task<bool> IsLocalUserAsync(Guid id, CancellationToken ct)`: `SELECT 1 FROM UserAccounts WHERE PrincipalID = @id`.
      A database failure throws, and the ledger turns that into `Unavailable`.
- **Deviation from the spec, recorded:** spec §7.2 says the service keeps only a hash of each key. The launcher must write
  local programs' keys into their INIs on every start, so local keys are stored in the owner-only settings file, like the
  database passwords, and compared by hash. Region Server pairing keys (plan 3) are stored as hashes only.

- [ ] **Step 1: Write the failing tests**

```csharp
// unit
[Fact] void Right_key_maps_to_its_regions()   // two programs; key of p1 → grant with p1's two region UUIDs only
[Fact] void Wrong_or_empty_key_fails()         // "", null, key with one char changed → false
[Fact] void Deleted_regions_are_not_covered()
[Fact] void Ensure_adds_missing_and_drops_stale_keys() // keys are 48 chars [A-Za-z0-9]; removed program's key gone
// integration (Presence rows inserted directly)
[Fact] Task Matching_session_in_region_is_Ok()
[Fact] Task Session_in_another_region_is_WrongRegion()
[Fact] Task Wrong_secure_session_is_NoSession()
[Fact] Task Zero_session_is_NoSession()
[Fact] Task Visitor_presence_row_counts()      // UserID = visitor UUID string, as the Gatekeeper writes it
[Fact] Task IsLocalUser_reads_UserAccounts()    // inserted id → true; other id → false
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Commit** — `feat(money): region keys and presence checks`

---

### Task 8: The money certificate

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyCertificate.cs`
- Test: `Launcher/GridGate.Launcher.Tests/MoneyCertificateTests.cs`

**Interfaces:**
- Produces:
  - `static class MoneyCertificate`:
    - `static X509Certificate2 LoadOrCreate(string pfxPath, TimeProvider time)`. It creates an ECDSA P-256 self-signed
      certificate when the file is missing or expires within 30 days, with subject `CN=GridGate money`, SAN `localhost`,
      `127.0.0.1` and `::1`, and a lifetime of 5 years. It saves the PFX with no password in the owner-only
      `paths.Security` folder; the folder protects it.
    - `static string Fingerprint(X509Certificate2 c)`: the upper-case hex SHA-256 of the DER bytes.
  - The file is `Path.Combine(paths.Security, "money.pfx")`.

- [ ] **Step 1: Write the failing tests:** `Creates_then_reuses` (same fingerprint on a second call);
  `Renews_30_days_before_expiry` (with a fake clock 4 years 11 months 2 days on, the fingerprint changes);
  `Has_loopback_names`; `Fingerprint_is_64_hex_chars`.
- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement with `CertificateRequest`** · [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Commit** — `feat(money): money service certificate`

---

### Task 9: The money service host and API

**Files:**
- Create: `Launcher/GridGate.Launcher/Money/MoneyServiceHost.cs`
- Create: `Launcher/GridGate.Launcher/Money/MoneyApi.cs` (request and response DTOs, and the endpoint handlers)
- Create: `Launcher/GridGate.Launcher/Money/MoneyAuthFailures.cs`
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneyServiceTests.cs`

**Interfaces:**
- Consumes: `MoneyLedger` (Tasks 3 to 6), `RegionKeyRing` and `PresenceCheck` (Task 7), `MoneyCertificate` (Task 8).
- Produces:
  - `sealed class MoneyServiceHost : IHostedService`. `StartAsync` does nothing when `MoneyRelease.Effective(settings().Money)`
    is Off. Otherwise it builds its own `WebApplication`: Kestrel `Listen(IPAddress.Loopback, s.MoneyPort, l => l.UseHttps(cert))`,
    no other middleware than the endpoints below, the same logging filters as the website, and request logging off.
  - All endpoints are `POST`, JSON in and out, under `/money/v1/`. The header is `Authorization: GridGateRegion <key>`.
  - **The API** (every request also has `txnId` (Guid), except `balance`, `economy` and `health`):

| Path | Body (besides `txnId`) | Checks | Ledger call |
|---|---|---|---|
| `balance` | `agentId, home, sessionId, secureSessionId` | key, presence in key's regions | `BalanceAsync` |
| `transfer` | `type` (`pay_avatar`/`pay_object`/`parcel_pass`), `from{agentId,home,sessionId,secureSessionId}`, `to{agentId,home}`, `amount, regionId, objectId?, objectName?` | key, presence of `from`, `regionId` in grant | `TransferAsync` |
| `payout` | `ownerId, to{agentId,home}, amount, regionId, objectId, objectName` | key, `regionId` in grant | `PayoutAsync` |
| `charge` | `type, payer{…session}, amount, payTo?` | key, presence of payer | `ChargeAsync` |
| `hold` | `kind, buyer{…session}, seller{agentId,home}, amount, regionId, objectId?, objectName?` | key, presence of buyer, `regionId` in grant | `HoldAsync` |
| `hold/complete`, `hold/return` | `holdId` | key; the hold's `region_id` in grant | `CompleteHoldAsync` / `ReturnHoldAsync` |
| `economy` | — | key | answers `currencySymbol`, `uploadFee`, `classifiedFee` from settings |
| `health` | — | key | `{ "ok": true }` |

  - **The answer** is always `{ "code": "<MoneyOutcome in snake_case>", "balance"?, "toBalance"?, "entryId"?, "holdId"? }` with
    HTTP 200. Authentication failures get a bare **401** with an empty body. Presence failures map to `no_session` or
    `wrong_region`, and a region outside the grant maps to `wrong_region`.
  - `MoneyAuthFailures`:
    - After **20** failed keys from one IP within 10 minutes, that IP gets 429 for 10 minutes.
    - One warning line is written per IP per window: the IP and the count, never the key.
  - Malformed JSON, unknown fields or wrong types get **400** with `{ "code": "bad_request" }`. Amounts must be JSON
    integers.
- Registration: `Program.cs`, next to the other hosted services (around line 412). Register `MoneyLedger`, `PresenceCheck`
  (`GridDb.OpenRobustAsync`) and `MoneyServiceHost` as singletons, plus `AddHostedService(sp => sp.GetRequiredService<MoneyServiceHost>())`.
  The ring is rebuilt from `Func<LauncherSettings>` on every request: cheap, and it picks up new programs.

- [ ] **Step 1: Write the failing tests.** They start `MoneyServiceHost` against `MoneyTestDatabase`, with a test-only
  override of the release gate. `MoneyServiceHost` takes a `Func<MoneyMode>` for the effective mode, and production passes
  `() => MoneyRelease.Effective(settings().Money)`. They use an `HttpClient` that pins the certificate fingerprint.

```csharp
[Fact] Task No_key_is_401_with_empty_body()
[Fact] Task Wrong_key_is_401()
[Fact] Task Twenty_bad_keys_then_429()
[Fact] Task Balance_for_avatar_in_my_region_is_ok()        // code "ok", balance 100
[Fact] Task Balance_for_avatar_elsewhere_is_wrong_region()
[Fact] Task Stale_session_is_no_session()                  // Presence row deleted
[Fact] Task Transfer_for_region_outside_grant_is_wrong_region()
[Fact] Task Repeated_txn_returns_same_answer()
[Fact] Task Hold_then_complete_over_http()
[Fact] Task Database_down_returns_unavailable_code()        // stop MariaDB: 200 with code "unavailable", body has no "Exception"/"password"
[Fact] Task Malformed_json_is_400_bad_request()
[Fact] Task Fractional_amount_is_400()                     // "amount": 1.5
[Fact] Task Listens_on_loopback_only()                     // connecting to the machine's LAN address on MoneyPort fails
[Fact] Task Off_mode_does_not_listen()                     // effective Off: nothing on MoneyPort
[Fact] Task Logs_never_contain_the_key()                   // capture ILogger output across the auth tests; key string absent
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Request fuzzing:** add `Random_bodies_never_500`, which sends 500 random JSON-ish bodies (seeded
  `Random(1234)`) to each endpoint with a valid key. Every answer must be 200, 400 or 401, never 500. Run it and see it pass.
- [ ] **Step 6: Commit** — `feat(money): region-facing money service over HTTPS`

---

### Task 10: Maintenance, provisioning on start, and the full check

**Files:**
- Create: `Launcher/GridGate.Launcher/Money/MoneyMaintenance.cs`
- Modify: `Launcher/GridGate.Launcher/Program.cs`, to register it next to `MoneyServiceHost`.
- Modify: wherever the grid start-up calls `DatabaseProvisioner.EnsureAsync`. Find it with `grep -rn "DatabaseProvisioner" Launcher/GridGate.Launcher.Core`.
  When the effective mode isn't Off: make sure `Db.MoneyPassword` is set (`UpdateSettings` with `Secrets.NewPassword()` if
  empty) before provisioning, then run `MoneySchema.EnsureAsync`.
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneyMaintenanceTests.cs`

**Interfaces:**
- Produces: `sealed class MoneyMaintenance(MoneyLedger ledger, Func<MoneyMode> effective, TimeProvider time, ILogger<MoneyMaintenance> log) : BackgroundService`.
  When the effective mode isn't Off it:
  - runs `SelfCheckAsync` once at start and then every 24 hours;
  - runs `ReturnExpiredHoldsAsync` every **30 seconds**;
  - logs a failed self-check as an error with the mismatching account ids and sums only;
  - logs `Unavailable` as a warning at most once per 5 minutes.
- The dashboard notice for a frozen grid comes in plan 2, with the Money page.
- **Backups:** spec §7.1 asks that `money` be backed up wherever `robust` and `opensim` are. The launcher has no database
  backup or export today; all three live in the same MariaDB data directory (`paths.MariaDbData`), so they are copied
  together by anything that copies it. There is nothing to add in this plan.

- [ ] **Step 1: Write the failing tests:** `Expired_hold_is_returned_by_the_sweep` (fake clock: advance 6 minutes, then 30
  seconds, and the hold is returned); `Startup_self_check_freezes_tampered_books`; `Off_does_nothing`.
- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Full check.** Run `dotnet build GridGate.sln -c Release`; expected `0 Error(s)`. Then run
  `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`, and
  `$env:GRIDGATE_REQUIRE_INTEGRATION='1'; dotnet test --project Launcher/GridGate.Launcher.IntegrationTests -c Release -- --filter-class "*Money*"`
  (with `GRIDGATE_MARIADB_BIN` set). Expected: all pass, with no skips in the money classes.
- [ ] **Step 6: Commit** — `feat(money): self-check and hold sweep in the background`
