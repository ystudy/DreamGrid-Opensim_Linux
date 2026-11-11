using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Resident;

/// <summary>
/// The one rule for which languages a resident may be shown or pick: the grid's offered list (<see cref="LanguageSettings.OfferedOrDefault"/>)
/// less any code GridGate has no culture for (a hand-edited unknown default). Every picker, page, email and saved-language check
/// asks here, so a later rule (only complete owner-added languages) changes one place.
/// </summary>
public sealed class OfferedLanguages(Func<LauncherSettings> settings)
{
    /// <summary>The languages offered now, in the owner's order.</summary>
    public IReadOnlyList<string> Current() => Of(settings().Languages);

    /// <summary>The grid's default language when it is offered, else English.</summary>
    public string Default() => DefaultOf(settings().Languages);

    /// <summary>The offered languages for <paramref name="s"/> (for callers that already hold the settings).</summary>
    public static IReadOnlyList<string> Of(LanguageSettings s) => [.. s.OfferedOrDefault().Where(Languages.HasCulture)];

    /// <summary>The default language for <paramref name="s"/> when it is offered, else English.</summary>
    public static string DefaultOf(LanguageSettings s) => Languages.Match(s.Default, Of(s)) ?? Languages.English;
}
