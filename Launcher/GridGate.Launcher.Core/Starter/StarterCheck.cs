using GridGate.Launcher.Core.Database;

namespace GridGate.Launcher.Core.Starter;

/// <summary>A check's result: passed, the slots that are missing, and permission warnings that don't fail it.</summary>
public sealed record CheckResult(bool Passed, IReadOnlyList<string> Missing, IReadOnlyList<string> PermissionWarnings);

/// <summary>
/// The two starter avatar checks (spec 2026-11-11-starter-avatars-design §5.5, §6.3). Robust's <c>EstablishAppearance</c> never
/// fails a sign-up: a missing worn item just leaves its slot empty. So the copy is compared slot by slot, by asset.
/// </summary>
public static class StarterCheck
{
    public const string NoOutfit = "No outfit stored: log in and choose Replace Outfit";
    public const string NotTheOutfit = "It isn't wearing its own outfit yet: log in, open its folder and choose Replace Outfit";
    private const uint Copy_ = 0x8000;
    private const uint Modify = 0x4000;

    /// <summary>OpenSim's default outfit (<c>AvatarWearable.DEFAULT_*_ASSET</c>), which every new account wears.</summary>
    private static readonly HashSet<string> DefaultAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "66c41e39-38f9-f75a-024e-585989bfab73", "d342e6c0-b9d2-11dc-95ff-0800200c9a66", "77c41e39-38f9-f75a-024e-585989bbabbb",
        "4bb6fa4d-1cd2-498a-a84c-95c1a0e745a7", "00000000-38f9-1111-024e-222222111110", "00000000-38f9-1111-024e-222222111120",
        "1578a2b1-5179-4b53-b618-fe00ca5a5594", "00000000-0000-2222-3333-100000001007",
    };

    /// <summary>
    /// A model is ready when it has a stored outfit, owns every item it wears, and is really wearing its own outfit (final review
    /// C1: a new model already wears OpenSim's default outfit, from its own inventory). With <paramref name="outfitFolders"/> (the
    /// folder its archive was loaded into, and every folder under it) every worn item must be in one of them; without, an outfit
    /// of only OpenSim's default wearables fails. Two attachments on one point fail too (final review I1): Robust's copy keeps only
    /// the last one. Items a next owner can't copy or modify are warned about (residents get the next-owner permissions) but don't
    /// fail it.
    /// </summary>
    public static CheckResult Model(IReadOnlyList<WornItem>? worn, IReadOnlyList<InventoryItemRow> modelItems, IReadOnlySet<Guid>? outfitFolders = null)
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
        foreach (var point in worn.Where(w => w.Slot.StartsWith("_ap_", StringComparison.Ordinal)).GroupBy(w => w.Slot).Where(g => g.Count() > 1))
        {
            var names = point.Select(w => byId.TryGetValue(w.ItemId, out var i) ? i.Name : w.ItemId.ToString());
            missing.Add($"{point.Key}: {string.Join(", ", names)} on one point; only one is copied to residents, so move the others to other points");
        }
        if (missing.Count == 0)
        {
            var wornRows = worn.Select(w => byId[w.ItemId]).ToList();
            var ownOutfit = outfitFolders is not null
                ? wornRows.All(r => outfitFolders.Contains(r.Folder))
                : worn.Any(w => w.Slot.StartsWith("_ap_", StringComparison.Ordinal)) || wornRows.Any(r => !DefaultAssets.Contains(r.AssetId ?? ""));
            if (!ownOutfit) missing.Add(NotTheOutfit);
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
