using System.Xml.Linq;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Email;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Resident;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The one list of languages (built in and added) and what each added language misses (plan 16b).</summary>
public sealed class LanguageCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly OwnerLanguages _store;
    private readonly LanguageCatalog _catalog;

    public LanguageCatalogTests()
    {
        _store = new OwnerLanguages(Path.Combine(_dir, "languages"), _time);
        _catalog = new LanguageCatalog(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string ResxPath(string project, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, project, "Resources", file)))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new FileNotFoundException(file), project, "Resources", file);
    }

    private static List<string> Keys(string path) =>
        [.. XDocument.Load(path).Root!.Elements("data").Select(e => (string)e.Attribute("name")!)];

    private void Add(string code, IEnumerable<string>? page = null, IEnumerable<string>? email = null, string value = "x") =>
        _store.Save(new OwnerLanguage(code,
            (page ?? []).ToDictionary(k => k, _ => value),
            (email ?? []).ToDictionary(k => k, _ => value),
            _time.GetUtcNow()));

    private OfferedLanguages Offered(LanguageSettings l) => new(() => TestSettings.Create() with { Languages = l }, _store);

    [Fact]
    public void PageKeys_match_the_English_resx() =>
        Assert.Equal(Keys(ResxPath("GridGate.Launcher", "ResidentText.resx")).Order(StringComparer.Ordinal), _catalog.PageKeys());

    [Fact]
    public void English_gives_the_English_text()
    {
        var key = _catalog.PageKeys()[0];
        Assert.False(string.IsNullOrEmpty(_catalog.English(key)));
        Assert.Throws<ArgumentException>(() => _catalog.English("No.Such.Key"));
    }

    [Fact]
    public void EmailKeys_exclude_staff_templates()
    {
        var keys = _catalog.EmailKeys();
        Assert.Equal(Keys(ResxPath("GridGate.Launcher.Core", "EmailText.resx")).Order(StringComparer.Ordinal), keys);
        Assert.Contains("confirm-email.Subject", keys);
        Assert.Contains("ExpiresIn.30m", keys);
        Assert.Contains("Reason.Other", keys);
        foreach (var id in EmailTemplates.All.Where(EmailTemplates.IsStaffEmail))
            Assert.DoesNotContain(keys, k => k.StartsWith(id + ".", StringComparison.Ordinal));
        Assert.Equal("30 minutes", _catalog.EnglishEmail("ExpiresIn.30m"));
    }

    [Fact]
    public void Missing_counts_every_untranslated_text()
    {
        var total = _catalog.PageKeys().Count + _catalog.EmailKeys().Count;
        Add("it", page: _catalog.PageKeys().Take(2));
        Assert.Equal(total - 2, _catalog.Missing("it"));
        Assert.Equal(total - 2, _catalog.MissingKeys("it").Count);
        Add("it", page: _catalog.PageKeys().Take(2), email: _catalog.EmailKeys().Take(3));
        Assert.Equal(total - 5, _catalog.Missing("it"));
    }

    [Fact]
    public void A_blank_value_counts_as_missing()
    {
        var total = _catalog.PageKeys().Count + _catalog.EmailKeys().Count;
        Add("it", page: _catalog.PageKeys().Take(2), value: "  ");
        Assert.Equal(total, _catalog.Missing("it"));
    }

    [Fact]
    public void Missing_ignores_unknown_keys_in_the_file()
    {
        var total = _catalog.PageKeys().Count + _catalog.EmailKeys().Count;
        Add("it", page: ["No.Such.Key", .. _catalog.PageKeys().Take(1)], email: ["nope.Subject"]);
        Assert.Equal(total - 1, _catalog.Missing("it"));
        Assert.DoesNotContain("No.Such.Key", _catalog.MissingKeys("it"));
    }

    [Fact]
    public void Missing_is_zero_for_built_in()
    {
        Assert.Equal(0, _catalog.Missing("de"));
        Assert.Empty(_catalog.MissingKeys("fr"));
    }

    [Fact]
    public void A_file_from_before_new_keys_counts_them_missing()
    {
        // Everything the file knew is translated; two keys GridGate has gained since are not in it.
        var keys = _catalog.PageKeys();
        Add("it", page: keys.Skip(2), email: _catalog.EmailKeys());
        Assert.Equal(2, _catalog.Missing("it"));
        Assert.Equal(keys.Take(2), _catalog.MissingKeys("it"));
    }

    [Fact]
    public void Current_includes_an_added_offered_language()
    {
        Add("it");
        var o = Offered(new LanguageSettings { Offered = ["en", "it"] });
        Assert.Equal(["en", "it"], o.Current());
        Assert.Equal(["en", "de", "es", "fr", "pt", "ru", "it"], o.All());
        Assert.False(o.IsBuiltIn("it"));
        Assert.True(o.IsBuiltIn("de"));
    }

    [Fact]
    public void An_added_region_code_is_matched_as_it_is_written()
    {
        Add("pt-PT");
        var o = Offered(new LanguageSettings { Offered = ["en", "pt-PT"] });
        Assert.Equal(["en", "pt-PT"], o.Current());
        Assert.Equal("pt-PT", Languages.Match("pt-pt", o.Current()));
    }

    [Fact]
    public void Current_skips_an_offered_code_with_no_file()
    {
        var o = Offered(new LanguageSettings { Offered = ["en", "it"] });
        Assert.Equal(["en"], o.Current());
        Assert.Equal(["en", "de", "es", "fr", "pt", "ru"], o.All());
    }

    [Fact]
    public void Default_falls_back_to_English_when_the_default_file_is_gone()
    {
        Add("it");
        var o = Offered(new LanguageSettings { Offered = ["en", "it"], Default = "it" });
        Assert.Equal("it", o.Default());
        _store.Delete("it");
        Assert.Equal("en", o.Default());
        Assert.Equal(["en"], o.Current());
    }
}
