using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>
/// Turns the operator's Markdown into HTML that is safe to show to anyone. Raw HTML is shown as text, a link keeps its
/// address only when that is <c>http:</c>, <c>https:</c>, <c>mailto:</c> or relative, and an image is shown as its alt text so
/// the page never loads anything from outside.
/// </summary>
public static class LegalMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseEmphasisExtras()
        .Build();

    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var document = Markdown.Parse(markdown, Pipeline);
        Sanitize(document);
        return document.ToHtml(Pipeline);
    }

    private static void Sanitize(MarkdownDocument document)
    {
        // Each list is taken first: the tree is changed while it is walked.
        foreach (var auto in document.Descendants<AutolinkInline>().ToList())
        {
            // <javascript:alert(1)> is a valid autolink; one that isn't allowed is shown as the text it was.
            if (!IsAllowed(auto.Url)) Replace(auto, new LiteralInline(auto.Url));
        }

        foreach (var table in document.Descendants<Table>())
            AlignWithClasses(table);

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage)
                Replace(link, new LiteralInline(PlainText(link)));
            else if (!IsAllowed(link.Url))
                WithoutAddress(link);
        }
    }

    /// <summary>
    /// A column written <c>|:--:|</c> or <c>|--:|</c> would be rendered with <c>style="text-align: ..."</c>, which the site's CSP
    /// blocks. The alignment is cleared and the cells get Bootstrap's <c>text-center</c> / <c>text-end</c> instead.
    /// </summary>
    private static void AlignWithClasses(Table table)
    {
        foreach (var row in table.OfType<TableRow>())
        {
            var column = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var align = (cell.ColumnIndex >= 0 ? cell.ColumnIndex : column) is var i && i < table.ColumnDefinitions.Count
                    ? table.ColumnDefinitions[i].Alignment
                    : null;
                if (align == TableColumnAlign.Center) cell.GetAttributes().AddClass("text-center");
                else if (align == TableColumnAlign.Right) cell.GetAttributes().AddClass("text-end");
                column += Math.Max(1, cell.ColumnSpan);
            }
        }
        foreach (var definition in table.ColumnDefinitions) definition.Alignment = null;
    }

    /// <summary>Puts <paramref name="with"/> where <paramref name="inline"/> was, dropping the old one and everything inside it.</summary>
    private static void Replace(Inline inline, Inline with)
    {
        inline.InsertBefore(with);
        inline.Remove();
    }

    /// <summary>The link's words kept, its address dropped: an <c>a</c> element with no href.</summary>
    private static void WithoutAddress(LinkInline link)
    {
        var children = link.ToList();
        foreach (var child in children) child.Remove();
        link.InsertBefore(new HtmlInline("<a>"));
        foreach (var child in children) link.InsertBefore(child);
        link.InsertBefore(new HtmlInline("</a>"));
        link.Remove();
    }

    private static string PlainText(LinkInline link)
    {
        var text = new System.Text.StringBuilder();
        foreach (var inline in link.Descendants<Inline>())
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case CodeInline code: text.Append(code.Content); break;
                case LineBreakInline: text.Append(' '); break;
            }
        }
        return text.ToString();
    }

    /// <summary>Whether a link may keep this address: no scheme (relative) or one of <see cref="AllowedSchemes"/>.</summary>
    private static bool IsAllowed(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        // Browsers ignore whitespace and control characters inside a scheme ("java\tscript:"), so they are ignored here too.
        var cleaned = new string(url.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
        var end = cleaned.IndexOfAny([':', '/', '?', '#']);
        if (end < 0 || cleaned[end] != ':') return true; // no scheme before the first / ? or #: a relative address
        return AllowedSchemes.Contains(cleaned[..end], StringComparer.OrdinalIgnoreCase);
    }
}
