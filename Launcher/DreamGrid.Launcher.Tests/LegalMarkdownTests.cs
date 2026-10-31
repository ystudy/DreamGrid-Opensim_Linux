using DreamGrid.Launcher.Core.Guests;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The operator's Markdown becomes HTML that is safe to show to anyone. The texts here are made up for the tests.</summary>
public sealed class LegalMarkdownTests
{
    [Fact]
    public void Headings_lists_links_render()
    {
        var html = LegalMarkdown.ToHtml("""
            # Test heading

            Some *test* and **strong** text with [a page](/contact), [a site](https://example.com/x) and [mail](mailto:test@example.com).

            - one
            - two

            1. first
            2. second
            """);

        Assert.Contains("<h1>Test heading</h1>", html);
        Assert.Contains("<em>test</em>", html);
        Assert.Contains("<strong>strong</strong>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<ol>", html);
        Assert.Contains("""<a href="/contact">a page</a>""", html);
        Assert.Contains("""<a href="https://example.com/x">a site</a>""", html);
        Assert.Contains("""<a href="mailto:test@example.com">mail</a>""", html);
    }

    [Fact]
    public void Tables_and_strikethrough_render_but_other_extras_are_off()
    {
        var html = LegalMarkdown.ToHtml("""
            | a | b |
            |---|---|
            | 1 | 2 |

            ~~gone~~

            Term
            : not a definition list
            """);

        Assert.Contains("<table>", html);
        Assert.Contains("<del>gone</del>", html);
        Assert.DoesNotContain("<dl>", html);
    }

    [Fact]
    public void Aligned_table_columns_use_classes_not_style_attributes()
    {
        var html = LegalMarkdown.ToHtml("""
            | left | centre | right |
            |:-----|:------:|------:|
            | 1 | 2 | 3 |
            """);

        Assert.DoesNotContain("style=", html); // the CSP blocks inline styles
        Assert.Contains("""<th>left</th>""", html);
        Assert.Contains("""<th class="text-center">centre</th>""", html);
        Assert.Contains("""<th class="text-end">right</th>""", html);
        Assert.Contains("""<td class="text-center">2</td>""", html);
        Assert.Contains("""<td class="text-end">3</td>""", html);
    }

    [Fact]
    public void Raw_html_and_script_links_are_inert()
    {
        var html = LegalMarkdown.ToHtml("""
            <script>alert(1)</script>

            <img src=x onerror=alert(1)>

            [x](javascript:alert(1))

            [y](JaVaScRiPt:alert(1))

            [z](  java&#9;script:alert(1))

            [v](vbscript:x) [d](data:text/html;base64,AAAA)

            [r][ref]

            [ref]: javascript:alert(2)
            """);

        Assert.DoesNotContain("<script", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript", html);
        Assert.DoesNotContain("data:", html);
        Assert.Contains("<a>x</a>", html);
        Assert.Contains("<a>y</a>", html);
        Assert.Contains("<a>r</a>", html);
        Assert.DoesNotContain("href=\"\"", html);
    }

    [Fact]
    public void Autolinks_keep_their_address_only_when_allowed()
    {
        var html = LegalMarkdown.ToHtml("<javascript:alert(1)> and <https://example.com/ok> and <test@example.com>");

        Assert.DoesNotContain("href=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("javascript:alert(1) and", html); // shown as the plain text it was
        Assert.Contains("""<a href="https://example.com/ok">https://example.com/ok</a>""", html);
        Assert.Contains("""<a href="mailto:test@example.com">test@example.com</a>""", html);
    }

    [Fact]
    public void Images_become_alt_text()
    {
        var html = LegalMarkdown.ToHtml("""
            Before ![the logo](https://example.com/logo.png) and ![](/local.png) and ![bad *one*](javascript:alert(1)) after.
            """);

        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("src=", html);
        Assert.DoesNotContain("logo.png", html);
        Assert.Contains("Before the logo and  and bad one after.", html);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData(null)]
    public void Nothing_renders_as_nothing(string? markdown) => Assert.Equal("", LegalMarkdown.ToHtml(markdown));
}
