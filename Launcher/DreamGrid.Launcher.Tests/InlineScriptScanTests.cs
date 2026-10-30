using System.Text.RegularExpressions;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// The page security policy has no 'unsafe-inline': every script is a file under wwwroot/js and every style is a class in
/// site.css. This scans the page sources so a new page cannot slip one back in.
/// </summary>
public sealed class InlineScriptScanTests
{
    // <script> without src=, an on…="…" event attribute, a style="…" attribute or a <style> block.
    private static readonly (string Name, Regex Pattern)[] Rules =
    [
        ("<script> without src", new(@"<script\b(?![^>]*\bsrc\s*=)", RegexOptions.IgnoreCase)),
        ("event handler attribute", new(@"\son[a-z]+\s*=\s*""", RegexOptions.IgnoreCase)),
        ("style attribute", new(@"\sstyle\s*=\s*""", RegexOptions.IgnoreCase)),
        ("<style> block", new(@"<style\b", RegexOptions.IgnoreCase)),
    ];

    [Fact]
    public void No_inline_script_or_handlers()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DreamGrid.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("DreamGrid.sln not found");
        var pages = Path.Combine(root, "Launcher", "DreamGrid.Launcher", "Pages");
        var files = Directory.GetFiles(pages, "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var found = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (var (name, pattern) in Rules)
                    if (pattern.IsMatch(lines[i]))
                        found.Add($"{Path.GetRelativePath(pages, file)}:{i + 1}: {name}: {lines[i].Trim()}");
        }

        Assert.True(found.Count == 0, "Inline script or style found (move it to wwwroot):\n" + string.Join('\n', found));
    }
}
