using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace DreamGrid.Launcher.Navigation;

/// <summary>
/// Picks a button's icon (a symbol in <c>wwwroot/img/icons.svg</c>) from the words on it, so every button gets one without
/// each page naming it. The first rule whose words begin the label wins; a label nothing matches gets <see cref="Fallback"/>.
/// </summary>
public static partial class ButtonIcons
{
    public const string Sprite = "/img/icons.svg";
    public const string Fallback = "chevron-right";

    // Order matters: a longer phrase sits above a shorter one that begins the same way ("save as oar" above "save").
    private static readonly (string Start, string Icon)[] Rules =
    [
        ("sign out", "sign-out"), ("sign in", "sign-in"), ("i'm in", "sign-in"), ("i’m in", "sign-in"),
        ("save as oar", "download"), ("save size", "resize"), ("save", "save"),
        ("cancel", "x"), ("close", "x"), ("deny", "x"), ("discard", "x"), ("shrink without saving", "x"), ("let it go", "x"),
        ("delete", "trash"), ("remove", "trash"), ("take down", "trash"), ("take these down", "trash"),
        ("search", "search"), ("find", "search"), ("look up", "search"),
        ("show it on the map", "map-pin"), ("show passwords", "eye"), ("show", "eye"), ("preview", "eye"),
        ("continue", "arrow-right"), ("next", "arrow-right"), ("go to", "arrow-right"), ("skip", "arrow-right"), ("open", "arrow-right"),
        ("back", "arrow-left"), ("previous", "arrow-left"), ("send back", "arrow-left"),
        ("up", "arrow-up"), ("down", "arrow-down"), ("move", "move"),
        ("copy", "copy"), ("duplicate", "copy"),
        ("power", "power"), ("turn on", "play"), ("turn off", "power"), ("start", "play"), ("try it", "play"), ("let ", "play"),
        ("unsuspend", "play"), ("suspend", "pause"), ("stop being the default", "star"), ("stop", "stop"), ("end", "stop"),
        ("restart", "restart"), ("reset", "restart"), ("new backup codes", "restart"), ("reopen", "restart"),
        ("undo", "undo"), ("redo", "redo"), ("restore", "undo"), ("put back", "undo"),
        ("send", "send"), ("test connection", "link"), ("join", "link"), ("point at", "link"), ("take it", "link"),
        ("add", "plus"), ("new", "plus"), ("create", "plus"),
        ("import", "upload"), ("upload", "upload"), ("publish", "upload"), ("download", "download"), ("export", "download"),
        ("change password", "key"), ("rename", "edit"), ("edit", "edit"), ("change", "edit"),
        ("make admin", "shield"), ("make supervisor", "shield"), ("give region rights", "shield"),
        ("make guest", "person"), ("make estate", "map-pin"), ("make the land again", "land"), ("make this the default", "star"),
        ("use as", "star"), ("use this", "check"),
        ("mark urgent", "flag"), ("not urgent", "flag"),
        ("oar", "box"), ("small", "resize"), ("medium", "resize"), ("large", "resize"),
        ("approve", "check"), ("accept", "check"), ("i accept", "check"), ("keep", "check"), ("set it aside", "check"), ("ok", "check"),
    ];

    /// <summary>The icon for a button that reads <paramref name="label"/>; null when it has no words.</summary>
    public static string? For(string label)
    {
        var text = Spaces().Replace(label, " ").Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        // "Yes, delete it" takes the icon of what it says yes to; a bare "Yes" is a tick.
        if (text.StartsWith("yes", StringComparison.Ordinal))
        {
            text = text[3..].TrimStart(',', ' ');
            if (text.Length == 0) return "check";
        }
        foreach (var (start, icon) in Rules)
        {
            if (text.StartsWith(start, StringComparison.Ordinal)
                && (start.EndsWith(' ') || text.Length == start.Length || !char.IsLetter(text[start.Length])))
                return icon;
        }
        return Fallback;
    }

    /// <summary>True for a Bootstrap button that shows words: class token <c>btn</c>, and not a text-only <c>btn-link</c>.</summary>
    public static bool IsButton(string classes)
    {
        var tokens = classes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Contains("btn") && !tokens.Contains("btn-link");
    }

    /// <summary>The words a rendered label shows: its HTML without tags, decoded.</summary>
    public static string TextOf(string html) => WebUtility.HtmlDecode(Tags().Replace(html, " "));

    public static string Svg(string icon) =>
        $"<svg class=\"btn-icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"{Sprite}#{icon}\"></use></svg>";

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex("<[^>]*>")] private static partial Regex Tags();
}

/// <summary>
/// Puts an icon at the start of every Bootstrap button (<c>class="btn …"</c>) on the server, so it needs no script (CSP).
/// <c>icon="name"</c> on the element picks the symbol; <c>icon="none"</c> leaves the button bare. A button that already
/// holds an <c>&lt;svg&gt;</c> keeps it.
/// </summary>
[HtmlTargetElement("button", Attributes = "class")]
[HtmlTargetElement("a", Attributes = "class")]
[HtmlTargetElement("summary", Attributes = "class")]
public sealed class ButtonIconTagHelper : TagHelper
{
    /// <summary>The symbol to show, or "none". Unset: chosen from the button's words.</summary>
    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (!output.Attributes.TryGetAttribute("class", out var cls) || !ButtonIcons.IsButton(Html(cls.Value))) return;
        if (Icon == "none") return;
        var content = output.IsContentModified ? output.Content.GetContent() : (await output.GetChildContentAsync()).GetContent();
        if (content.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return;
        var icon = string.IsNullOrEmpty(Icon) ? ButtonIcons.For(ButtonIcons.TextOf(content)) : Icon;
        if (icon is null) return;
        output.PreContent.AppendHtml(ButtonIcons.Svg(icon));
    }

    private static string Html(object? value)
    {
        switch (value)
        {
            case null: return "";
            case string s: return s;
            case IHtmlContent h:
                using (var w = new StringWriter())
                {
                    h.WriteTo(w, HtmlEncoder.Default);
                    return w.ToString();
                }
            default: return value.ToString() ?? "";
        }
    }
}

/// <summary>
/// <c>&lt;help-tip&gt;Words&lt;/help-tip&gt;</c>: a small "?" icon whose words show while the pointer is over it or it has
/// the keyboard focus (CSS only, site.css <c>.help-tip</c>). The words stay in the page for screen readers.
/// </summary>
[HtmlTargetElement("help-tip")]
public sealed class HelpTipTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        var classes = output.Attributes.TryGetAttribute("class", out var c) ? "help-tip " + c.Value : "help-tip";
        output.Attributes.SetAttribute("class", classes);
        output.Attributes.SetAttribute("tabindex", "0");
        output.PreContent.AppendHtml(
            $"<svg class=\"help-icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"{ButtonIcons.Sprite}#help\"></use></svg><span class=\"help-text\" role=\"tooltip\">");
        output.PostContent.AppendHtml("</span>");
    }
}
