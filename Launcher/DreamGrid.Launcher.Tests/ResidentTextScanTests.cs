using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// Resident pages (guest, support, sign-up, legal, welcome, language picker and the partials they render) take every
/// word from ResidentText.resx. This scans the page sources so a new page cannot slip hard-coded English back in.
/// The Razor is stripped down to its markup (code blocks, @expressions and comments are blanked), then every text node
/// and every title, placeholder, aria-label and alt value is checked for letters.
/// </summary>
public sealed class ResidentTextScanTests
{
    // Pages that are resident, relative to Pages/. Folders are scanned whole.
    private static readonly string[] ResidentFolders = ["Guest", "Support"];
    private static readonly string[] ResidentPages = ["Home.cshtml", "SignUp.cshtml", "Legal.cshtml", "Welcome.cshtml", "Language.cshtml"];

    // Resident layouts and partials that no page names with <partial>: a layout is named by Layout = "..." and the
    // ones below are found that way too, but listing them keeps the scan from quietly shrinking if a reference is renamed.
    private static readonly string[] ResidentPartials =
    [
        "_GuestLayout", "_LanguagePicker", "_LegalLinks", "_BotFields", "_LoginAddress",
        "_SplashLayout", "_SplashBlock", "_SplashBlocks",
    ];

    // _PlainHttpWarning is not listed: only staff pages (Index, Security/Legal) render it.
    // Staff-only partials: never resident, so never scanned even if a staff page next to them uses them.
    private static readonly string[] StaffPartials = ["_SignUpRows", "_JoinFound", "_HubLinks"];

    // Literal text that is the same in every language. Keep it short: only what the scan really finds. Each entry is removed from the text before the letter check.
    private static readonly (string Text, string Reason)[] Allowed =
    [
        ("Firestorm", "viewer name, the same in every language (Guest/Connect)"),
    ];

    private static readonly Regex Letters = new("[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S*", RegexOptions.Compiled);
    private static readonly Regex PartialRef = new(@"<partial\s+name\s*=\s*""([^""]+)""|PartialAsync\(\s*""([^""]+)""|Layout\s*=\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly string[] CheckedAttributes = ["title", "placeholder", "aria-label", "alt"];

    [Fact]
    public void No_hard_coded_English_on_resident_pages()
    {
        var pages = PagesDirectory();
        var files = ResidentFiles(pages);
        Assert.True(files.Count >= 20, $"Only {files.Count} resident files found; the discovery is broken.");

        var found = new List<string>();
        foreach (var file in files)
            foreach (var text in OffendingText(File.ReadAllText(file)))
                found.Add($"{Path.GetRelativePath(pages, file)}: \"{text}\"");

        Assert.True(found.Count == 0,
            "Hard-coded English on a resident page (move it to ResidentText.resx and use @T[\"...\"], or add a language-neutral name to the allow-list with a reason):\n"
            + string.Join('\n', found));
    }

    [Fact]
    public void Scanner_finds_text_in_every_razor_shape()
    {
        var razor = """
            @page
            @model Foo
            @* Razor comment with English *@
            @{
                var x = new { A = "code string", B = 1 };
                if (x.B > 0) { x = null; }
            }
            <p>@T["Key"] Leftover</p>
            @if (a) { <p>inside if</p> } else { <p>inside else</p> }
            @foreach (var i in list)
            {
                <li class="@(i ? "yes" : "no")">@i.Name</li>
                @:colon line
                <text>text block</text>
            }
            <input placeholder="@T["P"]" title="plain title" aria-label="@T["A"]" alt="alt words">
            <a href="mailto:a@b.com">@T["Mail"]</a>
            """;
        var found = OffendingText(razor);
        Assert.Equal(
            ["Leftover", "inside if", "inside else", "colon line", "text block", "plain title", "alt words"],
            found.Select(t => t.Trim()).ToArray());
    }

    private static string PagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DreamGrid.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("DreamGrid.sln not found");
        return Path.Combine(root, "Launcher", "DreamGrid.Launcher", "Pages");
    }

    /// <summary>The resident pages, plus every partial or layout they (transitively) name and the hand list above.</summary>
    private static List<string> ResidentFiles(string pages)
    {
        var all = Directory.GetFiles(pages, "*.cshtml", SearchOption.AllDirectories);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        void Add(string f) { if (files.Add(f)) queue.Enqueue(f); }

        foreach (var folder in ResidentFolders)
            foreach (var f in Directory.GetFiles(Path.Combine(pages, folder), "*.cshtml", SearchOption.AllDirectories)) Add(f);
        foreach (var page in ResidentPages) Add(Path.Combine(pages, page));
        foreach (var name in ResidentPartials)
            Add(all.Single(f => Path.GetFileNameWithoutExtension(f) == name));

        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            foreach (Match m in PartialRef.Matches(File.ReadAllText(file)))
            {
                var name = Path.GetFileNameWithoutExtension(m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value);
                if (StaffPartials.Contains(name)) continue;
                // A partial lives next to the page or in Shared.
                var hit = all.Where(f => Path.GetFileNameWithoutExtension(f) == name).ToList();
                var near = hit.FirstOrDefault(f => Path.GetDirectoryName(f) == Path.GetDirectoryName(file))
                           ?? hit.FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) == "Shared");
                if (near is not null) Add(near);
            }
        }
        return files.Where(f => !StaffPartials.Contains(Path.GetFileNameWithoutExtension(f)))
                    .Where(f => !Path.GetFileName(f).StartsWith("_ViewStart")).Order().ToList();
    }

    private static List<string> OffendingText(string razor)
    {
        var markup = RazorMarkup.Strip(razor);
        var doc = new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + markup + "</body></html>");
        var offending = new List<string>();
        foreach (var node in doc.Descendants())
        {
            if (node is IText text && text.ParentElement is { LocalName: not ("script" or "style") })
                Check(text.Data, offending);
            else if (node is IElement el)
                foreach (var attr in CheckedAttributes)
                    if (el.GetAttribute(attr) is { } value) Check(value, offending);
        }
        return offending;
    }

    private static void Check(string value, List<string> offending)
    {
        var rest = Url.Replace(value, " ");
        foreach (var (text, _) in Allowed) rest = rest.Replace(text, " ");
        if (Letters.IsMatch(rest)) offending.Add(value.Trim());
    }

    /// <summary>Keeps the markup of a .cshtml and blanks everything that is C#.</summary>
    private static class RazorMarkup
    {
        private static readonly string[] BlockKeywords = ["if", "foreach", "for", "while", "switch", "lock", "using", "try", "do"];
        private static readonly string[] DirectiveLines = ["page", "model", "inject", "layout", "namespace", "addTagHelper", "removeTagHelper", "tagHelperPrefix", "implements", "inherits", "attribute", "preservewhitespace", "using"];
        private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
            { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };

        public static string Strip(string source)
        {
            var s = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);
            var sb = new StringBuilder();
            var i = 0;
            Markup(s, ref i, sb, Stop.Never);
            return sb.ToString();
        }

        private enum Stop { Never, EndOfLine, ElementClosed }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Copies markup from i, blanking Razor, until the stop condition. Leaves i just past what it consumed.</summary>
        private static void Markup(string s, ref int i, StringBuilder sb, Stop stop)
        {
            var depth = 0;
            var started = false;
            while (i < s.Length)
            {
                var c = s[i];
                if (stop == Stop.EndOfLine && c == '\n') { return; }
                if (c == '@')
                {
                    if (i + 1 < s.Length && s[i + 1] == '@') { sb.Append(' '); i += 2; continue; }
                    if (i > 0 && IsIdent(s[i - 1])) { sb.Append(c); i++; continue; } // an e-mail address
                    sb.Append(' ');
                    Razor(s, ref i, sb);
                    continue;
                }
                if (c == '<' && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == '/' || s[i + 1] == '!'))
                {
                    var end = TagEnd(s, i);
                    var tag = s.Substring(i, end - i);
                    var m = Regex.Match(tag, @"^<(/?)([A-Za-z][A-Za-z0-9:-]*)");
                    // Blank Razor expressions inside the tag but keep the attribute text.
                    var cleaned = new StringBuilder();
                    var j = i;
                    while (j < end)
                    {
                        if (s[j] == '@' && j + 1 < end && s[j + 1] != '@' && !(j > i && IsIdent(s[j - 1])))
                        {
                            cleaned.Append(' ');
                            Razor(s, ref j, cleaned, inTag: true);
                        }
                        else { cleaned.Append(s[j]); j++; }
                    }
                    sb.Append(cleaned);
                    i = Math.Max(end, j);
                    if (m.Success && stop == Stop.ElementClosed)
                    {
                        var name = m.Groups[2].Value;
                        var closing = m.Groups[1].Length > 0;
                        var selfClosed = tag.EndsWith("/>") || Void.Contains(name);
                        if (closing) depth--;
                        else if (!selfClosed) depth++;
                        started = true;
                        if (depth <= 0) return;
                    }
                    continue;
                }
                sb.Append(c);
                i++;
                if (stop == Stop.ElementClosed && !started && !char.IsWhiteSpace(c)) started = true;
            }
        }

        /// <summary>The index just past the '>' that closes the tag at i, skipping quoted values and @(...) expressions.</summary>
        private static int TagEnd(string s, int i)
        {
            var j = i + 1;
            while (j < s.Length)
            {
                var c = s[j];
                if (c == '"' || c == '\'')
                {
                    j++;
                    while (j < s.Length && s[j] != c)
                    {
                        if (s[j] == '@' && j + 1 < s.Length && s[j + 1] == '(') j = SkipBalanced(s, j + 1);
                        else j++;
                    }
                    j++;
                }
                else if (c == '@' && j + 1 < s.Length && s[j + 1] == '(') j = SkipBalanced(s, j + 1);
                else if (c == '>') return j + 1;
                else j++;
            }
            return s.Length;
        }

        /// <summary>Handles the Razor construct after an '@' at s[i]; for a code block, markup inside it is appended to sb.</summary>
        private static void Razor(string s, ref int i, StringBuilder sb, bool inTag = false)
        {
            i++; // the '@'
            if (i >= s.Length) return;
            if (s[i] == ':') { i++; Markup(s, ref i, sb, Stop.EndOfLine); return; }
            if (s[i] == '(') { i = SkipBalanced(s, i); return; }
            if (s[i] == '{') { CodeBlock(s, ref i, sb); return; }
            if (!IsIdentStart(s[i])) return;

            var start = i;
            while (i < s.Length && IsIdent(s[i])) i++;
            var word = s[start..i];
            if (word == "await") { while (i < s.Length && s[i] == ' ') i++; if (i < s.Length && IsIdentStart(s[i])) { sb.Append(' '); Razor(s, ref i, sb, inTag: true); } return; }

            if (!inTag)
            {
                if (word == "section")
                {
                    while (i < s.Length && s[i] != '{') i++;
                    CodeBlock(s, ref i, sb);
                    return;
                }
                if (word is "code" or "functions")
                {
                    while (i < s.Length && s[i] != '{') i++;
                    CodeBlock(s, ref i, sb);
                    return;
                }
                if (word == "using" && !NextIs(s, i, '(')) { SkipLine(s, ref i); return; }
                if (word != "using" && DirectiveLines.Contains(word, StringComparer.Ordinal)) { SkipLine(s, ref i); return; }
                if (BlockKeywords.Contains(word, StringComparer.Ordinal))
                {
                    BlockAfterKeyword(s, ref i, sb);
                    return;
                }
            }

            // An implicit expression: Name, Name.Member, Name[...], Name(...), chained.
            while (i < s.Length)
            {
                if (s[i] == '(' || s[i] == '[') { i = SkipBalanced(s, i); continue; }
                if (s[i] == '.' && i + 1 < s.Length && IsIdentStart(s[i + 1])) { i++; while (i < s.Length && IsIdent(s[i])) i++; continue; }
                if (s[i] == '?' && i + 1 < s.Length && (s[i + 1] == '.' || s[i + 1] == '[')) { i++; continue; }
                break;
            }
        }

        private static bool NextIs(string s, int i, char c)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return i < s.Length && s[i] == c;
        }

        private static void SkipLine(string s, ref int i)
        {
            while (i < s.Length && s[i] != '\n') i++;
        }

        /// <summary>After @if / @foreach / ...: skip the header, then the block, then any else / else if / catch / finally / while.</summary>
        private static void BlockAfterKeyword(string s, ref int i, StringBuilder sb)
        {
            while (true)
            {
                while (i < s.Length && s[i] != '{' && s[i] != '(') i++;
                if (i < s.Length && s[i] == '(') i = SkipBalanced(s, i);
                while (i < s.Length && s[i] != '{') i++;
                if (i >= s.Length) return;
                CodeBlock(s, ref i, sb);
                var j = i;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                var m = Regex.Match(s[j..Math.Min(s.Length, j + 12)], @"^(else|catch|finally)\b");
                if (!m.Success) return;
                i = j + m.Length;
                if (m.Value == "else")
                {
                    var k = i;
                    while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
                    if (s.AsSpan(k).StartsWith("if")) i = k + 2;
                }
            }
        }

        /// <summary>s[i] is the '{'. Skips the C# inside, but lets markup elements and @: lines through to sb. Leaves i past the '}'.</summary>
        private static void CodeBlock(string s, ref int i, StringBuilder sb)
        {
            var depth = 0;
            var last = '{'; // last significant char of C#
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '{') { depth++; last = c; i++; continue; }
                if (c == '}') { depth--; last = c; i++; if (depth == 0) return; continue; }
                if (c == '"' || c == '\'') { i = SkipString(s, i); last = ';'; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { SkipLine(s, ref i); continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? s.Length : e + 2; continue; }
                if (c == '@' && i + 1 < s.Length && s[i + 1] == ':') { i += 2; Markup(s, ref i, sb, Stop.EndOfLine); continue; }
                if (c == '@' && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '$')) { i++; continue; }
                if (c == '@') { sb.Append(' '); Razor(s, ref i, sb); last = ';'; continue; }
                if (c == '<' && i + 1 < s.Length && char.IsLetter(s[i + 1]) && last is '{' or '}' or ';' or ':')
                {
                    Markup(s, ref i, sb, Stop.ElementClosed);
                    sb.Append('\n');
                    last = ';';
                    continue;
                }
                if (c == '$' && i + 1 < s.Length && s[i + 1] == '"') { i++; continue; }
                if (!char.IsWhiteSpace(c)) last = c;
                i++;
            }
        }

        /// <summary>s[i] is an opening ( or [ : returns the index just past its match, skipping strings.</summary>
        private static int SkipBalanced(string s, int i)
        {
            var depth = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '"' || c == '\'') { i = SkipString(s, i); continue; }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') { depth--; if (depth == 0) return i + 1; }
                i++;
            }
            return s.Length;
        }

        /// <summary>s[i] is a quote: returns the index just past the string (verbatim @"" and escaped \" are handled).</summary>
        private static int SkipString(string s, int i)
        {
            var q = s[i];
            var verbatim = i > 0 && s[i - 1] == '@';
            i++;
            while (i < s.Length)
            {
                if (s[i] == '\\' && !verbatim) { i += 2; continue; }
                if (s[i] == q)
                {
                    if (verbatim && i + 1 < s.Length && s[i + 1] == q) { i += 2; continue; }
                    return i + 1;
                }
                i++;
            }
            return s.Length;
        }
    }
}
