using System.Text.RegularExpressions;
using System.Xml.Linq;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Security;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// Checks every translation file of the resident pages (<c>ResidentText.&lt;language&gt;.resx</c>) and the resident emails
/// (<c>EmailText.&lt;language&gt;.resx</c>) against the English one. The files are found by their names, so these tests pass while no
/// translation exists and hold every one that is added to the same rules.
/// </summary>
public sealed partial class TranslationsTests
{
    /// <summary>Every translation starts with an XML comment containing this (before <c>&lt;root&gt;</c> or as its first child).</summary>
    public const string AiDraftNote = "drafted by AI";

    private static readonly string Launcher = FindLauncherFolder();

    private static readonly string ResidentFolder = Path.Combine(Launcher, "DreamGrid.Launcher", "Resources");
    private static readonly string EmailFolder = Path.Combine(Launcher, "DreamGrid.Launcher.Core", "Resources");

    private static string FindLauncherFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var launcher = Path.Combine(dir.FullName, "Launcher");
            if (File.Exists(Path.Combine(launcher, "DreamGrid.Launcher", "Resources", "ResidentText.resx"))) return launcher;
        }
        throw new DirectoryNotFoundException("The Launcher folder with Resources/ResidentText.resx was not found above " + AppContext.BaseDirectory);
    }

    private static readonly string[] ResidentTemplates = [.. EmailTemplates.All.Where(id => !EmailTemplates.IsStaffEmail(id))];

    /// <summary>The language files of a text, as (language, path): <c>ResidentText.de.resx</c> is ("de", ...).</summary>
    private static List<(string Language, string Path)> Translations(string folder, string name) =>
        [.. Directory.GetFiles(folder, name + ".*.resx")
            .Select(p => (Language: Path.GetFileNameWithoutExtension(p)[(name.Length + 1)..], Path: p))
            .OrderBy(t => t.Language, StringComparer.Ordinal)];

    private static Dictionary<string, string> Entries(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static readonly (string Name, string Folder, string English, Regex Placeholder)[] Texts =
    [
        ("ResidentText", ResidentFolder, "ResidentText.resx", NumberedPlaceholder()),
        ("EmailText", EmailFolder, "EmailText.resx", NamedPlaceholder()),
    ];

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex NumberedPlaceholder();

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex NamedPlaceholder();

    // The tests below find translations by their file names, so a deleted file would pass them; these name the built-in ones.
    public static TheoryData<string> BuiltInTranslations() => [.. Core.Languages.BuiltIn.Where(c => c != Core.Languages.English)];

    [Theory]
    [MemberData(nameof(BuiltInTranslations))]
    public void Every_built_in_language_has_both_translation_files(string code)
    {
        Assert.True(File.Exists(Path.Combine(ResidentFolder, $"ResidentText.{code}.resx")), $"ResidentText.{code}.resx is missing");
        Assert.True(File.Exists(Path.Combine(EmailFolder, $"EmailText.{code}.resx")), $"EmailText.{code}.resx is missing");
    }

    // The satellite assembly is built and loads: the runtime gives the translated value, not the English fallback.
    [Theory]
    [MemberData(nameof(BuiltInTranslations))]
    public void Every_built_in_language_loads_at_run_time_in_both_projects(string code)
    {
        var culture = Core.Languages.Culture(code);
        foreach (var (name, folder, english, manager) in new[]
        {
            ("ResidentText", ResidentFolder, "ResidentText.resx",
                new System.Resources.ResourceManager("DreamGrid.Launcher.Resources.ResidentText", typeof(ResidentText).Assembly)),
            ("EmailText", EmailFolder, "EmailText.resx",
                new System.Resources.ResourceManager("DreamGrid.Launcher.Core.Resources.EmailText", typeof(EmailText).Assembly)),
        })
        {
            var en = Entries(Path.Combine(folder, english));
            var translated = Entries(Path.Combine(folder, $"{name}.{code}.resx"));
            var key = en.Keys.First(k => translated.TryGetValue(k, out var v) && v.Length > 0 && v != en[k]);
            var atRunTime = manager.GetString(key, culture);
            Assert.NotEqual(en[key], atRunTime);
            Assert.Equal(translated[key], atRunTime);
        }
    }

    [Fact]
    public void English_texts_exist_and_are_not_empty()
    {
        foreach (var (_, folder, english, _) in Texts)
        {
            var path = Path.Combine(folder, english);
            Assert.True(File.Exists(path), path);
            Assert.NotEmpty(Entries(path));
        }
        Assert.True(Entries(Path.Combine(ResidentFolder, "ResidentText.resx")).Count >= 294);
    }

    [Fact]
    public void Every_language_has_every_key()
    {
        foreach (var (name, folder, english, _) in Texts)
        {
            var keys = Entries(Path.Combine(folder, english)).Keys.ToHashSet();
            foreach (var (language, path) in Translations(folder, name))
            {
                var have = Entries(path).Keys.ToHashSet();
                Assert.True(keys.SetEquals(have),
                    $"{name}.{language}: missing [{string.Join(", ", keys.Except(have).Order())}], not in English [{string.Join(", ", have.Except(keys).Order())}]");
            }
        }
    }

    [Fact]
    public void Placeholders_match_English()
    {
        foreach (var (name, folder, english, placeholder) in Texts)
        {
            var en = Entries(Path.Combine(folder, english));
            foreach (var (language, path) in Translations(folder, name))
                foreach (var (key, value) in Entries(path))
                {
                    if (!en.TryGetValue(key, out var original)) continue;
                    var want = placeholder.Matches(original).Select(m => m.Groups[1].Value).ToHashSet();
                    var got = placeholder.Matches(value).Select(m => m.Groups[1].Value).ToHashSet();
                    Assert.True(want.SetEquals(got), $"{name}.{language} {key}: English has [{string.Join(", ", want.Order())}], this has [{string.Join(", ", got.Order())}]");
                }
        }
    }

    [Fact]
    public void Required_words_are_in_every_language()
    {
        var files = new List<(string Language, Dictionary<string, string> Entries)> { ("en", Entries(Path.Combine(EmailFolder, "EmailText.resx"))) };
        files.AddRange(Translations(EmailFolder, "EmailText").Select(t => (t.Language, Entries(t.Path))));
        foreach (var (language, entries) in files)
            foreach (var id in ResidentTemplates)
            {
                Assert.True(entries.TryGetValue(id + ".Subject", out var subject), $"EmailText.{language} {id}.Subject");
                Assert.True(entries.TryGetValue(id + ".Body", out var body), $"EmailText.{language} {id}.Body");
                var missing = EmailTemplates.MissingWords(id, subject, body);
                Assert.True(missing.Count == 0, $"EmailText.{language} {id} lacks {string.Join(", ", missing)}");
            }
    }

    [Fact]
    public void Each_translation_starts_with_the_AI_draft_note()
    {
        foreach (var (name, folder, _, _) in Texts)
            foreach (var (language, path) in Translations(folder, name))
            {
                var doc = XDocument.Load(path);
                var note = doc.Nodes().TakeWhile(n => n is not XElement).OfType<XComment>().FirstOrDefault()
                    ?? doc.Root!.Nodes().FirstOrDefault() as XComment;
                Assert.True(note is not null && note.Value.Contains(AiDraftNote, StringComparison.Ordinal),
                    $"{name}.{language}.resx has no comment with \"{AiDraftNote}\" before <root> or as its first child");
            }
    }

    [Fact]
    public void Language_files_are_named_for_built_in_languages()
    {
        foreach (var (name, folder, _, _) in Texts)
            foreach (var (language, _) in Translations(folder, name))
                Assert.Contains(language, DreamGrid.Launcher.Core.Languages.BuiltIn);
    }

    [Fact]
    public void English_email_text_is_read_through_the_resource_manager()
    {
        Assert.Equal("30 minutes", EmailTemplates.Words("ExpiresIn.30m", "en"));
        Assert.Equal("24 hours", EmailTemplates.Words("ExpiresIn.24h", "en"));
        Assert.Equal("Confirm your email for {GridName}", EmailTemplates.Default("confirm-email", "en").Subject);
    }

    [Fact]
    public void Every_resident_email_is_in_the_English_text_and_staff_emails_are_not()
    {
        var english = Entries(Path.Combine(EmailFolder, "EmailText.resx"));
        Assert.Equal(18, ResidentTemplates.Length);
        foreach (var id in EmailTemplates.All)
            Assert.Equal(!EmailTemplates.IsStaffEmail(id), english.ContainsKey(id + ".Subject") && english.ContainsKey(id + ".Body"));
    }

    [Fact]
    public void English_reason_words_are_the_staff_pages_words()
    {
        foreach (var reason in Enum.GetValues<TakedownReason>())
            Assert.Equal(TakedownRules.ReasonWords(reason), EmailTemplates.Words("Reason." + reason, "en"));
    }
}
