namespace DreamGrid.Launcher.Core.Security;

/// <summary>An item as the sender's page shows it: no holder, no folder, and the creator masked when they hold it.</summary>
public sealed record SenderItem(string Name, string Type, string Creator, DateTimeOffset RemovedAt, DateTimeOffset? PutBackAt);

/// <summary>Takedown wording and limits, and what the notice's sender may see (spec 2026-11-04-inventory-takedown-design).</summary>
public static class TakedownRules
{
    public const int MaxNoticeText = 4000;
    public const int MaxField = 1000;
    public const string MaskedCreator = "a resident";

    public static string ReasonWords(TakedownReason r) => r switch
    {
        TakedownReason.Copyright => "Copyright (DMCA)",
        TakedownReason.Illegal => "Illegal content",
        TakedownReason.Rules => "Breaks the grid's rules",
        _ => "Other",
    };

    /// <summary>Whether Tell the resident starts ticked: for Copyright and Rules.</summary>
    public static bool DefaultTell(TakedownReason r) => r is TakedownReason.Copyright or TakedownReason.Rules;

    /// <summary>Whether the resident may be told at all: never for illegal content, so a suspect isn't warned.</summary>
    public static bool MayTell(TakedownReason r) => r != TakedownReason.Illegal;

    /// <summary>An OpenSim asset type in words.</summary>
    public static string TypeWords(int? assetType) => assetType switch
    {
        0 => "texture", 1 => "sound", 2 => "calling card", 3 => "landmark", 5 => "clothing", 6 => "object", 7 => "notecard", 10 => "script",
        13 => "body part", 20 => "animation", 21 => "gesture", 49 => "mesh", 56 => "settings", 57 => "material", _ => "item",
    };

    /// <summary>Why the notice can't be saved, or null.</summary>
    public static string? NoticeProblem(TakedownNotice n)
    {
        if (string.IsNullOrWhiteSpace(n.SenderName)) return "Who sent the notice is needed.";
        if ((n.Text ?? "").Length > MaxNoticeText) return $"The notice text can be at most {MaxNoticeText} characters.";
        if (new[] { n.SenderName, n.Organisation, n.Address, n.Reference }.Any(f => (f ?? "").Length > MaxField))
            return $"Each field can be at most {MaxField} characters.";
        return null;
    }

    /// <summary>The case's items for the sender: the creator is "a resident" when the creator is one of the case's people.</summary>
    public static IReadOnlyList<SenderItem> SenderView(TakedownCase c) =>
    [
        .. c.Items.Select(i => new SenderItem(i.Name, TypeWords(i.AssetType),
            c.People.Any(p => i.CreatorId.StartsWith(p.Principal.ToString("D"), StringComparison.OrdinalIgnoreCase)) ? MaskedCreator : i.CreatorName,
            i.RemovedAt, i.PutBackAt)),
    ];

    /// <summary>Audit actions only the owner and admins see in a person's history.</summary>
    public static bool IsTakedownAudit(string action) =>
        action == "inventory-viewed" || action.StartsWith("takedown-", StringComparison.Ordinal);
}
