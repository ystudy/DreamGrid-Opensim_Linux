using DreamGrid.Launcher.Core.Ini;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class IniDocumentTests
{
    private static string N(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void Set_replaces_value_and_keeps_comments()
    {
        var d = IniDocument.Parse("[Const]\n; note\nBaseURL=http://x\n");
        d.Set("Const", "BaseURL", "http://y");
        Assert.Equal("[Const]\n; note\nBaseURL=http://y\n", N(d.ToText()));
    }

    [Fact]
    public void Set_replaces_last_active_occurrence_only()
    {
        var d = IniDocument.Parse("[Chat]\nenabled=true\nenabled=true\n");
        d.Set("Chat", "enabled", "false");
        Assert.Equal("[Chat]\nenabled=true\nenabled=false\n", N(d.ToText()));
    }

    [Fact]
    public void Set_ignores_commented_lines()
    {
        var d = IniDocument.Parse("[Chat]\n; enabled=true\n");
        d.Set("Chat", "enabled", "false");
        Assert.Equal("[Chat]\n; enabled=true\nenabled=false\n", N(d.ToText()));
    }

    [Fact]
    public void Set_appends_missing_section()
    {
        var d = IniDocument.Parse("[A]\nx=1\n");
        d.Set("SmartStart", "Enabled", "False");
        Assert.EndsWith("[SmartStart]\nEnabled=False\n", N(d.ToText()));
        Assert.StartsWith("[A]\nx=1\n", N(d.ToText()));
    }

    [Fact]
    public void Set_appends_after_last_active_line_of_section()
    {
        var d = IniDocument.Parse("[A]\nx=1\n; tail\n\n[B]\ny=2");
        d.Set("A", "z", "3");
        Assert.Equal("[A]\nx=1\nz=3\n; tail\n\n[B]\ny=2", N(d.ToText()));
    }

    [Fact]
    public void Section_and_key_match_case_insensitively()
    {
        var d = IniDocument.Parse("[Const]\nBaseURL=a\n");
        d.Set("const", "baseurl", "b");
        Assert.Equal("b", d.Get("CONST", "BASEURL"));
        Assert.Equal("[Const]\nBaseURL=b\n", N(d.ToText()));
    }

    [Fact]
    public void Get_strips_quotes()
    {
        var d = IniDocument.Parse("[Architecture]\n    Include-Architecture=\"config-include/GridHypergrid.ini\"\n");
        Assert.Equal("config-include/GridHypergrid.ini", d.Get("Architecture", "Include-Architecture"));
        Assert.Null(d.Get("Architecture", "missing"));
    }

    [Fact]
    public void Value_keeps_equals_and_semicolons()
    {
        var d = IniDocument.Parse("[DatabaseService]\nConnectionString = \"Data Source=h;Port=3306;\"\n");
        Assert.Equal("Data Source=h;Port=3306;", d.Get("DatabaseService", "ConnectionString"));
        d.Set("DatabaseService", "ConnectionString", "\"Data Source=z;Port=1;\"");
        Assert.Equal("[DatabaseService]\nConnectionString = \"Data Source=z;Port=1;\"\n", N(d.ToText()));
    }

    [Fact]
    public void CommentOut_disables_every_active_line()
    {
        var d = IniDocument.Parse("[S]\nk=1\nk=2\nother=3\n");
        d.CommentOut("S", "k");
        Assert.Equal("[S]\n; k=1\n; k=2\nother=3\n", N(d.ToText()));
        Assert.Null(d.Get("S", "k"));
    }

    [Fact]
    public void ReplaceBlock_inserts_markers_when_missing_and_replaces_between_them()
    {
        var d = IniDocument.Parse("[PayPal]\na=1\n");
        d.ReplaceBlock("PayPal", "; BEGIN", "; END", new[] { "b=2", "c=3" });
        Assert.Equal("[PayPal]\na=1\n; BEGIN\nb=2\nc=3\n; END\n", N(d.ToText()));
        d.ReplaceBlock("PayPal", "; BEGIN", "; END", new[] { "d=4" });
        Assert.Equal("[PayPal]\na=1\n; BEGIN\nd=4\n; END\n", N(d.ToText()));
    }

    [Fact]
    public void ActiveEntries_lists_only_active_lines()
    {
        var d = IniDocument.Parse("top=0\n[A]\n; c=1\nx=\"2\"\n");
        Assert.Equal(new[] { ("", "top", "0"), ("A", "x", "2") }, d.ActiveEntries().ToArray());
    }

    [Fact]
    public void Unchanged_lines_keep_their_own_terminators()
    {
        var text = "[A]\r\nx=1\ny=2\r\n";
        var d = IniDocument.Parse(text);
        Assert.Equal(text, d.ToText());
        d.Set("A", "x", "9");
        Assert.Equal("[A]\r\nx=9\ny=2\r\n", d.ToText());
    }

    [Fact]
    public void Save_writes_utf8_without_bom()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini");
        try
        {
            IniDocument.Parse("[A]\nx=é\n").Save(path);
            var bytes = File.ReadAllBytes(path);
            Assert.NotEqual(0xEF, bytes[0]);
            Assert.Equal("[A]\nx=é\n", File.ReadAllText(path));
            Assert.Equal("é", IniDocument.Load(path).Get("A", "x"));
        }
        finally { File.Delete(path); }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DreamGrid.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DreamGrid.sln not found");
    }

    [Theory]
    [InlineData("bin/Robust.HG.ini.proto")]
    [InlineData("bin/config-include/Proto/Robust/OpenSim.ini")]
    [InlineData("bin/config-include/Proto/Robust/GridCommon.ini")]
    [InlineData("bin/config-include/Proto/Robust/GridHypergrid.ini")]
    [InlineData("bin/config-include/Proto/Robust/Grid.ini")]
    public void Round_trip_of_every_prototype_is_lossless(string rel)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), rel));
        Assert.Equal(N(text), N(IniDocument.Parse(text).ToText()));
        Assert.Equal(text, IniDocument.Parse(text).ToText());
    }

    [Theory]
    [InlineData("A", "x", "1\n[Evil]")]
    [InlineData("A", "x", "1\rmore")]
    [InlineData("A", "x\ny", "1")]
    [InlineData("A]\n[B", "x", "1")]
    public void Set_rejects_line_breaks(string section, string key, string value)
    {
        // A value with a line break would add lines (even sections) to the generated INI.
        var d = IniDocument.Parse("[A]\nx=0\n");
        Assert.Throws<ArgumentException>(() => d.Set(section, key, value));
        Assert.Equal("[A]\nx=0\n", N(d.ToText()));
    }
}
