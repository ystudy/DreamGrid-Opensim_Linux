namespace GridGate.Launcher.Core.Money;

/// <summary>
/// Who holds an account: an avatar UUID plus their home grid (money design §7.1). An empty <see cref="Home"/> is a resident
/// of this grid; anything else is a visitor's home grid address.
/// </summary>
public readonly record struct AccountRef(Guid AgentId, string Home)
{
    public static AccountRef Resident(Guid id) => new(id, "");

    public bool IsResident => Home.Length == 0;
}

/// <summary>Every answer the ledger gives. Only <see cref="Ok"/> moved anything.</summary>
public enum MoneyOutcome
{
    Ok,
    NotEnough,
    Frozen,
    AccountFrozen,
    Unavailable,
    BadAmount,
    NotAllowed,
    OverDailyLimit,
    UnknownHold,
    HoldClosed,
    NoWallet,
    DuplicateReference,
}

/// <summary>
/// A ledger answer. <see cref="FromBalance"/> is the payer's balance after (or the balance asked for), <see cref="ToBalance"/>
/// the payee's.
/// </summary>
public sealed record MoneyResult(MoneyOutcome Outcome, long? FromBalance = null, long? ToBalance = null, long? EntryId = null, long? HoldId = null)
{
    public static readonly MoneyResult Unavailable = new(MoneyOutcome.Unavailable);

    public static MoneyResult Of(MoneyOutcome outcome) => new(outcome);
}

/// <summary>The entry types (money design §7.4), as stored in <c>entries.type</c>.</summary>
public static class EntryType
{
    public const string StartingBalance = "starting_balance";
    public const string Allowance = "allowance";
    public const string StaffGift = "staff_gift";
    public const string StaffTake = "staff_take";
    public const string BoughtCoins = "bought_coins";
    public const string PayAvatar = "pay_avatar";
    public const string PayObject = "pay_object";
    public const string ObjectSale = "object_sale";
    public const string LandSale = "land_sale";
    public const string ParcelPass = "parcel_pass";
    public const string ScriptPayout = "script_payout";
    public const string UploadFee = "upload_fee";
    public const string GroupJoinFee = "group_join_fee";
    public const string ClassifiedFee = "classified_fee";
    public const string Hold = "hold";
    public const string HoldCompleted = "hold_completed";
    public const string HoldReturned = "hold_returned";
    public const string Refund = "refund";
    public const string WalletMerge = "wallet_merge";
    public const string FeeMoved = "fee_moved";
}

/// <summary>One avatar paying another avatar or an object's owner. The region checks the avatar's session before asking.</summary>
public sealed record TransferRequest(
    Guid TxnId,
    string Type,
    AccountRef From,
    AccountRef To,
    long Amount,
    Guid? RegionId = null,
    Guid? ObjectId = null,
    string? ObjectName = null);
