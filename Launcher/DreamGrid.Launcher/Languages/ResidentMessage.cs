using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher;

/// <summary>
/// What a service class wants a resident page to say: a <c>ResidentText</c> key and its values. The service keeps its English
/// sentence for logs and tests; the page turns this into the page's language with <see cref="In"/>.
/// </summary>
public sealed record ResidentMessage(string Key, params object[] Args)
{
    /// <summary>The message in the language of the current request.</summary>
    public string In(IStringLocalizer<ResidentText> t) => t[Key, Args].Value;
}
