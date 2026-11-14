using System.Text.Json.Serialization;

namespace GridGate.Launcher.Core.Money;

/// <summary>The grid's money system, as the owner chose it (money design §1); saved by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MoneyMode>))]
public enum MoneyMode { Off, Play, Real, Gloebit }

/// <summary>
/// The owner's money settings (money design §7.5). <paramref name="ProgramKeys"/> maps a region program's instance to its
/// region key (§7.2); null reads as none.
/// </summary>
public sealed record MoneySettings(
    MoneyMode Mode = MoneyMode.Off,
    string CurrencySymbol = "C$",
    string CurrencyName = "coins",
    long StartingBalancePlay = 100,
    long StartingBalanceReal = 0,
    long WeeklyAllowance = 0,
    long UploadFee = 0,
    long ClassifiedFee = 0,
    bool ScriptPayouts = true,
    long DailyPayoutLimit = 5000,
    bool VisitorWallets = true,
    long MaxPayment = 1_000_000,
    Dictionary<string, string>? ProgramKeys = null)
{
    private static readonly IReadOnlyDictionary<string, string> NoKeys = new Dictionary<string, string>();

    /// <summary><see cref="ProgramKeys"/>, never null.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Keys => ProgramKeys ?? NoKeys;

    /// <summary>What a new resident starts with in <paramref name="effective"/> mode; 0 unless Play or Real.</summary>
    public long StartingBalance(MoneyMode effective) => effective switch
    {
        MoneyMode.Play => StartingBalancePlay,
        MoneyMode.Real => StartingBalanceReal,
        _ => 0,
    };
}
