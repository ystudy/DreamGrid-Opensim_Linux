namespace GridGate.Launcher.Core.Money;

/// <summary>
/// The owner's safety gate (money design §12). Money stays Off in every build until the owner has approved the security
/// review: the approving change flips <see cref="PlayCoinsApproved"/> (Play coins and Gloebit), and a later one, after the
/// human review, flips <see cref="RealCoinsApproved"/>. A hand-edited settings file can't get around it.
/// </summary>
public static class MoneyRelease
{
    public const bool PlayCoinsApproved = false;
    public const bool RealCoinsApproved = false;

    /// <summary>The mode the grid actually runs in: <see cref="MoneyMode.Off"/> unless the chosen mode is approved.</summary>
    public static MoneyMode Effective(MoneySettings m) => Effective(m, PlayCoinsApproved, RealCoinsApproved);

    /// <summary>The gate with the approvals passed in, so tests can check both sides of it.</summary>
    public static MoneyMode Effective(MoneySettings m, bool playApproved, bool realApproved) => m.Mode switch
    {
        MoneyMode.Play or MoneyMode.Gloebit when playApproved => m.Mode,
        MoneyMode.Real when playApproved && realApproved => MoneyMode.Real,
        _ => MoneyMode.Off,
    };
}
