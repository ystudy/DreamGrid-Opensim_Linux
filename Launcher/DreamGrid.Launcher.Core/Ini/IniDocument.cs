using System.Text;

namespace DreamGrid.Launcher.Core.Ini;

/// <summary>
/// Comment-preserving INI document. Lines are kept verbatim (with their own terminators);
/// only lines touched by an edit change. Follows OpenSim/Nini rules: a line whose first
/// non-blank character is ';' or '#' is a comment, a ';' inside a value is part of the value,
/// the last active occurrence of a key wins, and a section that appears twice is one section.
/// </summary>
public sealed class IniDocument
{
    private enum Kind { Blank, Comment, Section, Entry, Other }

    private sealed class Line
    {
        public string Text;
        public string Terminator;
        public Line(string text, string terminator) { Text = text; Terminator = terminator; }
    }

    private readonly List<Line> _lines = new();

    private IniDocument() { }

    public static IniDocument Parse(string text)
    {
        var doc = new IniDocument();
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                doc._lines.Add(new Line(text.Substring(start), ""));
                break;
            }
            int end = nl;
            string term = "\n";
            if (end > start && text[end - 1] == '\r') { end--; term = "\r\n"; }
            doc._lines.Add(new Line(text.Substring(start, end - start), term));
            start = nl + 1;
        }
        return doc;
    }

    public static IniDocument Load(string path) => Parse(File.ReadAllText(path));

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.Append(l.Text).Append(l.Terminator);
        return sb.ToString();
    }

    public void Save(string path) =>
        File.WriteAllText(path, ToText(), new UTF8Encoding(false));

    // ---- classification -------------------------------------------------

    private static Kind Classify(string text, out string name, out string value)
    {
        name = ""; value = "";
        var t = text.Trim();
        if (t.Length == 0) return Kind.Blank;
        if (t[0] == ';' || t[0] == '#') return Kind.Comment;
        if (t[0] == '[')
        {
            int close = t.IndexOf(']');
            if (close > 0) { name = t.Substring(1, close - 1).Trim(); return Kind.Section; }
            return Kind.Other;
        }
        int eq = t.IndexOf('=');
        if (eq <= 0) return Kind.Other;
        name = t.Substring(0, eq).Trim();
        value = t.Substring(eq + 1).Trim();
        return Kind.Entry;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string v) =>
        v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v.Substring(1, v.Length - 2) : v;

    private IEnumerable<(int Index, Kind Kind, string Section, string Name, string Value)> Scan()
    {
        string section = "";
        for (int i = 0; i < _lines.Count; i++)
        {
            var k = Classify(_lines[i].Text, out var name, out var value);
            if (k == Kind.Section) section = name;
            yield return (i, k, section, name, value);
        }
    }

    private int LastActiveEntry(string section, string key)
    {
        int found = -1;
        foreach (var e in Scan())
            if (e.Kind == Kind.Entry && Same(e.Section, section) && Same(e.Name, key)) found = e.Index;
        return found;
    }

    /// <summary>
    /// Insertion point for a new line: the last active entry of the last occurrence of the section; when that
    /// occurrence has no entries, its last non-blank line (so comments directly under the header stay above).
    /// -1 if the section is absent.
    /// </summary>
    private int SectionAppendPoint(string section)
    {
        int headerOfLast = -1, lastEntryInLast = -1, lastNonBlankInLast = -1;
        foreach (var e in Scan())
        {
            if (e.Kind == Kind.Section)
            {
                if (Same(e.Name, section)) { headerOfLast = e.Index; lastEntryInLast = -1; lastNonBlankInLast = e.Index; }
            }
            else if (headerOfLast >= 0 && Same(e.Section, section))
            {
                if (e.Kind == Kind.Entry) lastEntryInLast = e.Index;
                if (e.Kind != Kind.Blank) lastNonBlankInLast = e.Index;
            }
        }
        return lastEntryInLast >= 0 ? lastEntryInLast : lastNonBlankInLast;
    }

    private void InsertAfter(int index, IEnumerable<string> texts)
    {
        if (_lines[index].Terminator.Length == 0) _lines[index].Terminator = Environment.NewLine;
        _lines.InsertRange(index + 1, texts.Select(t => new Line(t, Environment.NewLine)));
    }

    private void AppendSection(string section, IEnumerable<string> body)
    {
        if (_lines.Count > 0)
        {
            var last = _lines[^1];
            if (last.Terminator.Length == 0) last.Terminator = Environment.NewLine;
            if (last.Text.Trim().Length != 0) _lines.Add(new Line("", Environment.NewLine));
        }
        _lines.Add(new Line("[" + section + "]", Environment.NewLine));
        foreach (var b in body) _lines.Add(new Line(b, Environment.NewLine));
    }

    // ---- public API -----------------------------------------------------

    public void Set(string section, string key, string value)
    {
        int idx = LastActiveEntry(section, key);
        if (idx >= 0)
        {
            var text = _lines[idx].Text;
            int p = text.IndexOf('=') + 1;
            while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
            _lines[idx].Text = text.Substring(0, p) + value;
            return;
        }
        int at = SectionAppendPoint(section);
        if (at >= 0) InsertAfter(at, new[] { key + "=" + value });
        else AppendSection(section, new[] { key + "=" + value });
    }

    public string? Get(string section, string key)
    {
        int idx = LastActiveEntry(section, key);
        if (idx < 0) return null;
        Classify(_lines[idx].Text, out _, out var value);
        return Unquote(value);
    }

    public void CommentOut(string section, string key)
    {
        foreach (var e in Scan().ToList())
            if (e.Kind == Kind.Entry && Same(e.Section, section) && Same(e.Name, key))
                _lines[e.Index].Text = "; " + _lines[e.Index].Text;
    }

    public void ReplaceBlock(string section, string startMarker, string endMarker, IEnumerable<string> lines)
    {
        var body = lines.ToList();
        string sm = startMarker.Trim(), em = endMarker.Trim();
        int start = -1, end = -1;
        foreach (var e in Scan())
        {
            if (!Same(e.Section, section)) continue;
            var t = _lines[e.Index].Text.Trim();
            if (start < 0) { if (e.Kind == Kind.Comment && t == sm) start = e.Index; }
            else if (e.Kind == Kind.Comment && t == em) { end = e.Index; break; }
        }
        if (start >= 0 && end >= 0)
        {
            _lines.RemoveRange(start + 1, end - start - 1);
            _lines.InsertRange(start + 1, body.Select(b => new Line(b, Environment.NewLine)));
            return;
        }
        var block = new List<string> { startMarker };
        block.AddRange(body);
        block.Add(endMarker);
        int at = SectionAppendPoint(section);
        if (at >= 0) InsertAfter(at, block);
        else AppendSection(section, block);
    }

    public IEnumerable<(string Section, string Key, string Value)> ActiveEntries()
    {
        foreach (var e in Scan())
            if (e.Kind == Kind.Entry) yield return (e.Section, e.Name, Unquote(e.Value));
    }
}
