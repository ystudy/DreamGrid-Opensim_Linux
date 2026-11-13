namespace GridGate.Launcher.Core.Splash;

/// <summary>Which stored pictures a splash design refers to (spec 2026-11-01-viewer-splash-design §4.4).</summary>
public static class SplashPictures
{
    /// <summary>Every picture name in <paramref name="design"/>: page background, block backgrounds, picture blocks, place pictures.</summary>
    public static IReadOnlySet<string> UsedBy(SplashDesign? design)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (design is null) return names;
        // Null-safe throughout: a hand-edited file can hold a null anywhere.
        if (design.Background?.Picture?.Name is { } page) names.Add(page);
        foreach (var b in design.Blocks ?? [])
        {
            if (b is null) continue;
            if (b.Background?.Name is { } background) names.Add(background);
            if (b.Content?.Picture?.Name is { } picture) names.Add(picture);
            foreach (var place in b.Content?.Places ?? [])
                if (place?.Picture is { } placed) names.Add(placed);
        }
        return names;
    }

    /// <summary>The names the draft or the published design uses: what may not be deleted, and what the clean-up keeps.</summary>
    public static IReadOnlySet<string> InUse(SplashStore store)
    {
        var names = new HashSet<string>(UsedBy(store.Draft()?.Design), StringComparer.Ordinal);
        names.UnionWith(UsedBy(store.Published()?.Design));
        return names;
    }
}
