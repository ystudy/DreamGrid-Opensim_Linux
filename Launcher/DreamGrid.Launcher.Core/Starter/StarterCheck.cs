using DreamGrid.Launcher.Core.Database;

namespace DreamGrid.Launcher.Core.Starter;

/// <summary>A check's result: passed, the slots that are missing, and permission warnings that don't fail it.</summary>
public sealed record CheckResult(bool Passed, IReadOnlyList<string> Missing, IReadOnlyList<string> PermissionWarnings);

/// <summary>
/// The two starter avatar checks (spec 2026-11-11-starter-avatars-design §5.5, §6.3). Robust's <c>EstablishAppearance</c> never
/// fails a sign-up: a missing worn item just leaves its slot empty. So the copy is compared slot by slot, by asset.
/// </summary>
public static class StarterCheck
{
    public const string NoOutfit = "No outfit stored: log in and choose Replace Outfit";
    private const uint Copy_ = 0x8000;
    private const uint Modify = 0x4000;

    /// <summary>
    /// A model is ready when it has a stored outfit and owns every item it wears. Items a next owner can't copy or modify are
    /// warned about (residents get the next-owner permissions) but don't fail it.
    /// </summary>
    public static CheckResult Model(IReadOnlyList<WornItem>? worn, IReadOnlyList<InventoryItemRow> modelItems)
    {
        if (worn is null || worn.Count == 0) return new(false, [NoOutfit], []);
        var byId = modelItems.ToDictionary(i => i.ItemId);
        var missing = new List<string>();
        var warnings = new List<string>();
        foreach (var w in worn)
        {
            if (!byId.TryGetValue(w.ItemId, out var item))
            {
                if (!missing.Contains(w.Slot)) missing.Add(w.Slot);
                continue;
            }
            var next = item.NextPermissions ?? 0;
            if ((next & Copy_) == 0) warnings.Add($"{item.Name}: residents can't copy");
            if ((next & Modify) == 0) warnings.Add($"{item.Name}: residents can't modify");
        }
        return new(missing.Count == 0, missing, warnings);
    }

    /// <summary>
    /// A resident's copy is complete when, for every slot the model wears, they wear items of the same assets in that slot (a slot
    /// holding several attachments compares them all).
    /// </summary>
    public static CheckResult Copy(IReadOnlyList<WornItem> model, IReadOnlyList<InventoryItemRow> modelItems, IReadOnlyList<WornItem>? resident,
        IReadOnlyList<InventoryItemRow> residentItems)
    {
        var modelAssets = Assets(model, modelItems);
        var residentAssets = Assets(resident ?? [], residentItems);
        var missing = new List<string>();
        foreach (var (slot, wanted) in modelAssets)
        {
            var have = residentAssets.TryGetValue(slot, out var h) ? h : [];
            var left = new List<string?>(have);
            if (!wanted.All(a => a is not null && left.Remove(a))) missing.Add(slot);
        }
        return new(missing.Count == 0 && resident is { Count: > 0 }, resident is null ? [NoOutfit] : missing, []);
    }

    private static Dictionary<string, List<string?>> Assets(IReadOnlyList<WornItem> worn, IReadOnlyList<InventoryItemRow> items)
    {
        var byId = items.GroupBy(i => i.ItemId).ToDictionary(g => g.Key, g => g.First());
        var result = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        foreach (var w in worn)
        {
            if (!result.TryGetValue(w.Slot, out var list)) result[w.Slot] = list = [];
            list.Add(byId.TryGetValue(w.ItemId, out var row) ? row.AssetId?.ToLowerInvariant() : null);
        }
        return result;
    }
}
