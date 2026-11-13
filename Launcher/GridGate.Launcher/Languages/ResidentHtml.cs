using System.Text.RegularExpressions;
using GridGate.Launcher.Core.Guests;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher;

/// <summary>
/// A sentence of resident text with a link or a bit of markup inside it. The sentence is a <c>ResidentText</c> entry with
/// <c>{0}</c>, <c>{1}</c> where the pieces go, so each language puts them where its grammar wants them. The words are HTML-encoded,
/// never written raw: only the pieces (built here from encoded text) are markup.
/// </summary>
public static partial class ResidentHtml
{
    /// <summary>The sentence <paramref name="key"/>, with its placeholders replaced by <paramref name="pieces"/>.</summary>
    public static IHtmlContent With(this IStringLocalizer<ResidentText> t, string key, params IHtmlContent[] pieces)
    {
        var sentence = t[key].Value;
        var html = new HtmlContentBuilder();
        var at = 0;
        foreach (Match m in Placeholder().Matches(sentence))
        {
            html.Append(sentence[at..m.Index]);
            if (int.TryParse(m.Groups[1].Value, out var i) && i < pieces.Length) html.AppendHtml(pieces[i]);
            else html.Append(m.Value);
            at = m.Index + m.Length;
        }
        html.Append(sentence[at..]);
        return html;
    }

    /// <summary>A link: the text is encoded.</summary>
    public static IHtmlContent Link(string href, string text)
    {
        var a = new TagBuilder("a");
        a.Attributes["href"] = href;
        a.InnerHtml.Append(text);
        return a;
    }

    /// <summary>A <c>code</c> element: the text is encoded.</summary>
    public static IHtmlContent Code(string text)
    {
        var code = new TagBuilder("code");
        code.InnerHtml.Append(text);
        return code;
    }

    /// <summary>A <c>strong</c> element: the text is encoded.</summary>
    public static IHtmlContent Strong(string text)
    {
        var strong = new TagBuilder("strong");
        strong.InnerHtml.Append(text);
        return strong;
    }

    /// <summary>The title of a legal page (Terms, Privacy, Cookies, Contact) in the page's language.</summary>
    public static string LegalTitle(this IStringLocalizer<ResidentText> t, string page) => page switch
    {
        LegalPages.Terms => t["Legal.Terms"].Value,
        LegalPages.Privacy => t["Legal.Privacy"].Value,
        LegalPages.Cookies => t["Legal.Cookies"].Value,
        LegalPages.Contact => t["Legal.Contact"].Value,
        _ => LegalPages.Title(page),
    };

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();
}
