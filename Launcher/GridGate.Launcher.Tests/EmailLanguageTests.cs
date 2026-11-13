using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Email;
using GridGate.Launcher.Resident;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The language an email to a resident goes out in: theirs when the grid still offers it, else the grid's default.</summary>
public sealed class EmailLanguageTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"gg-emaillang-{Guid.NewGuid():N}.json");
    private readonly Guid _ann = Guid.NewGuid();

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
    }

    private (EmailLanguage Language, ResidentLanguages Saved) Make(IReadOnlyList<string>? offered = null, string defaultLanguage = "en")
    {
        var saved = new ResidentLanguages(_file);
        var s = TestSettings.Create() with { Languages = new LanguageSettings { Offered = offered ?? ["en", "de", "fr"], Default = defaultLanguage } };
        var none = new OwnerLanguages(Path.Combine(Path.GetTempPath(), "gg-none-" + Guid.NewGuid().ToString("N")), TimeProvider.System);
        return (new EmailLanguage(new OfferedLanguages(() => s, none), saved), saved);
    }

    [Fact]
    public void Saved_language_is_used()
    {
        var (language, saved) = Make();
        saved.Set(_ann, "de");
        Assert.Equal("de", language.ForResident(_ann));
    }

    [Fact]
    public void Saved_language_no_longer_offered_gives_the_default()
    {
        var (language, saved) = Make(offered: ["en", "fr"], defaultLanguage: "fr");
        saved.Set(_ann, "de");
        Assert.Equal("fr", language.ForResident(_ann));
    }

    [Fact]
    public void No_saved_language_gives_the_default()
    {
        var (language, _) = Make(defaultLanguage: "fr");
        Assert.Equal("fr", language.ForResident(_ann));
        Assert.Equal("fr", language.Default());
    }

    [Fact]
    public void A_saved_regional_tag_is_matched_to_an_offered_language()
    {
        var (language, saved) = Make();
        saved.Set(_ann, "de-at");
        Assert.Equal("de", language.ForResident(_ann));
    }

    [Fact]
    public void ForRequest_uses_the_page_language()
    {
        var (language, _) = Make();
        var ctx = new DefaultHttpContext();
        ctx.Items["gg-lang"] = "de";
        Assert.Equal("de", language.ForRequest(ctx));
    }

    [Fact]
    public void ForRequest_with_a_language_not_offered_gives_the_default()
    {
        var (language, _) = Make(offered: ["en", "fr"], defaultLanguage: "fr");
        var ctx = new DefaultHttpContext();
        ctx.Items["gg-lang"] = "de";
        Assert.Equal("fr", language.ForRequest(ctx));
    }

    [Fact]
    public void ForRequest_with_no_resident_page_gives_the_grid_default()
    {
        var (language, _) = Make(defaultLanguage: "fr");
        Assert.Equal("fr", language.ForRequest(new DefaultHttpContext()));
    }

    [Fact]
    public void ForResident_with_a_fallback_uses_the_saved_language_first()
    {
        var (language, saved) = Make();
        saved.Set(_ann, "de");
        Assert.Equal("de", language.ForResident(_ann, "fr"));
    }

    [Fact]
    public void ForResident_with_a_fallback_uses_it_when_nothing_is_saved()
    {
        var (language, _) = Make();
        Assert.Equal("fr", language.ForResident(_ann, "fr"));
    }

    [Fact]
    public void ForResident_with_a_fallback_not_offered_gives_the_default()
    {
        var (language, _) = Make(offered: ["en", "de"], defaultLanguage: "en");
        Assert.Equal("en", language.ForResident(_ann, "fr"));
    }
}
