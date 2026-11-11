using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Resident;

/// <summary>
/// The one rule for which languages a resident may be shown or pick: the grid's offered list (<see cref="LanguageSettings.OfferedOrDefault"/>)
/// of the built-in languages and the ones the owner added (<see cref="OwnerLanguages"/>, which must still have a file), less any code
/// GridGate has no culture for (a hand-edited unknown default). A default that is not on the offered list at all is kept when the system
/// knows it (a hand edit); one that is on the list but is neither built in nor added any more is not. Every picker, page, email and
/// saved-language check asks here.
/// </summary>
public sealed class OfferedLanguages(Func<LauncherSettings> settings, OwnerLanguages added)
{
    /// <summary>The languages offered now, in the owner's order.</summary>
    public IReadOnlyList<string> Current()
    {
        var s = settings().Languages;
        var addedNow = added.Codes();
        var listed = s.Offered ?? Languages.BuiltIn;
        return [.. s.OfferedOrDefault(addedNow)
            .Where(c => Languages.HasCulture(c) && (IsBuiltIn(c) || addedNow.Contains(c, StringComparer.OrdinalIgnoreCase) || !listed.Contains(c, StringComparer.OrdinalIgnoreCase)))];
    }

    /// <summary>The grid's default language when it is offered, else English.</summary>
    public string Default() => Languages.Match(settings().Languages.Default, Current()) ?? Languages.English;

    /// <summary>The built-in languages and the added ones, offered or not: built in first, then added in order.</summary>
    public IReadOnlyList<string> All() => [.. Languages.BuiltIn, .. added.Codes()];

    /// <summary>Whether <paramref name="code"/> ships with GridGate.</summary>
    public bool IsBuiltIn(string code) => Languages.BuiltIn.Contains(code, StringComparer.Ordinal);
}
