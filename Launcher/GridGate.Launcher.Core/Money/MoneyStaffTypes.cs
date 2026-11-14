namespace GridGate.Launcher.Core.Money;

/// <summary>Who on the staff did something, and why. The reason is required (money design §9.1).</summary>
public sealed record StaffAction(string StaffName, string Reason);

/// <summary>
/// Where the grid's coins are (money design §9.1). <see cref="InExistence"/> is every coin the Grid bank ever let out, so it
/// equals the sum of the other four.
/// </summary>
public sealed record MoneyTotals(long InExistence, long Residents, long Visitors, long Fees, long Holding, long MovedToday);

/// <summary>
/// One line of an account's history, from that account's side: <see cref="Amount"/> is negative when coins left it.
/// <see cref="Other"/> is the other account, or null for a system account.
/// </summary>
public sealed record HistoryLine(long EntryId, DateTimeOffset At, string Type, AccountRef? Other, long Amount, long BalanceAfter, string? ObjectName, string? Reason);

/// <summary>An account whose stored balance doesn't match its entries.</summary>
public sealed record BalanceMismatch(long AccountId, long Stored, long FromEntries);

/// <summary>The books' check (money design §7.1): all balances add up to zero, and each one matches its entries.</summary>
public sealed record SelfCheckResult(bool Ok, long Sum, IReadOnlyList<BalanceMismatch> Mismatches);
