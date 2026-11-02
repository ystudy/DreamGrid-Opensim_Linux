using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Splash;

/// <summary>A saved design: <see cref="Version"/> counts the draft's saves, or the publishes for the published copy.</summary>
public sealed record SplashDocument(SplashDesign Design, int Version, string SavedBy, DateTimeOffset Saved);

public abstract record SplashSaveResult
{
    public sealed record Saved(SplashDocument Draft) : SplashSaveResult;
    /// <summary>Someone saved since the editor loaded the draft: nothing was written.</summary>
    public sealed record Conflict(int CurrentVersion) : SplashSaveResult;
    public sealed record Invalid(IReadOnlyList<string> Problems) : SplashSaveResult;
}

/// <summary>
/// The splash design's draft and published copies, <c>Data/splash/draft.json</c> and <c>published.json</c> (spec
/// 2026-11-01-viewer-splash-design §4.1, §5). Residents only ever see the published copy; staff save drafts, and a save
/// based on an older draft version is refused, so two editors never overwrite each other. Safe to call from any thread.
/// </summary>
public sealed class SplashStore
{
    // JsonStateFile needs a value for "no file", so each document sits in a holder.
    private sealed record SplashFile(SplashDocument? Document);

    private readonly JsonStateFile<SplashFile> _draft, _published;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly Lock _gate = new();
    private int _warnedInvalid;

    public SplashStore(LauncherPaths paths, TimeProvider time, ILogger<SplashStore>? log = null)
    {
        _time = time;
        _log = log;
        _draft = new(Path.Combine(paths.Splash, "draft.json"), () => new(null), log);
        _published = new(Path.Combine(paths.Splash, "published.json"), () => new(null), log);
    }

    /// <summary>Whether a picture name is in the picture store (plan 12b sets it; until then there are no pictures).</summary>
    public Func<string, bool> PictureExists { get; init; } = _ => false;

    public SplashDocument? Draft() => _draft.Load().Document;

    /// <summary>Null when nothing was published, or when the file fails <see cref="SplashRules.Check"/> (warned once per process).</summary>
    public SplashDocument? Published()
    {
        var doc = _published.Load().Document;
        if (doc is null) return null;
        var problems = SplashRules.Check(doc.Design, PictureExists);
        if (problems.Count == 0) return doc;
        if (Interlocked.Exchange(ref _warnedInvalid, 1) == 0)
            _log?.LogWarning("The published splash page fails its check ({Problem}); the default page is shown instead.", problems[0]);
        return null;
    }

    /// <param name="basedOnVersion">The draft version the editor started from; 0 when it started without a draft.</param>
    public SplashSaveResult SaveDraft(SplashDesign design, int basedOnVersion, string savedBy)
    {
        var problems = SplashRules.Check(design, PictureExists);
        if (problems.Count > 0) return new SplashSaveResult.Invalid(problems);
        lock (_gate)
        {
            var current = Draft()?.Version ?? 0;
            if (basedOnVersion != current) return new SplashSaveResult.Conflict(current);
            var doc = new SplashDocument(design, current + 1, savedBy, _time.GetUtcNow());
            Write(_draft, doc);
            return new SplashSaveResult.Saved(doc);
        }
    }

    /// <summary>Copies the draft to the published copy with the next published version; null when there is no draft.</summary>
    public SplashDocument? Publish(string publishedBy)
    {
        lock (_gate)
        {
            if (Draft() is not { } draft) return null;
            // The counter is read from the file even when it fails the check, so a version is never used twice.
            var next = (_published.Load().Document?.Version ?? 0) + 1;
            var doc = new SplashDocument(draft.Design, next, publishedBy, _time.GetUtcNow());
            Write(_published, doc);
            return doc;
        }
    }

    /// <summary>Drops the draft; the editor then starts again from the published copy (or the default page).</summary>
    public void Discard()
    {
        lock (_gate) Write(_draft, null);
    }

    // A design that can't be written would be lost without a word, so a failed write is an error for the caller.
    private static void Write(JsonStateFile<SplashFile> file, SplashDocument? doc)
    {
        if (!file.Save(new SplashFile(doc))) throw new IOException("The splash page could not be saved.");
    }
}
