using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class LanguagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dgtest-" + Guid.NewGuid().ToString("N"));

    public LanguagesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData("de", "de")] [InlineData("DE", "de")] [InlineData("de-AT", "de")] [InlineData("pt-BR", "pt")] [InlineData("pt-PT", "pt")]
    [InlineData("ru-RU", "ru")] [InlineData("it", null)] [InlineData("", null)] [InlineData(null, null)]
    [InlineData("../../etc", null)] [InlineData("*", null)]
    public void Match_finds_an_offered_language(string? tag, string? expected) =>
        Assert.Equal(expected, Languages.Match(tag, Languages.BuiltIn));

    [Fact] public void Match_ignores_a_language_that_is_not_offered() => Assert.Null(Languages.Match("de", ["en", "fr"]));
    [Fact] public void Match_refuses_a_tag_longer_than_35_chars() => Assert.Null(Languages.Match("de-" + new string('a', 40), Languages.BuiltIn));

    [Fact]
    public void NativeName_is_the_languages_own_name()
    {
        Assert.Equal("Deutsch", Languages.NativeName("de"));
        Assert.Equal("Русский", Languages.NativeName("ru"));
    }

    [Fact] public void None_of_the_built_in_languages_is_right_to_left() => Assert.All(Languages.BuiltIn, c => Assert.False(Languages.IsRightToLeft(c)));
    [Fact] public void Arabic_is_right_to_left() => Assert.True(Languages.IsRightToLeft("ar"));
    [Fact] public void Portuguese_uses_Brazilian_dates_and_numbers() => Assert.Equal("pt-BR", Languages.Culture("pt").Name);

    [Fact]
    public void Default_settings_offer_all_six_with_English_default()
    {
        var s = new LanguageSettings();
        Assert.Equal(Languages.BuiltIn, s.OfferedOrDefault());
        Assert.Equal("en", s.Default);
    }

    [Fact]
    public void OfferedOrDefault_always_contains_the_default() =>
        Assert.Contains("fr", new LanguageSettings { Offered = ["de"], Default = "fr" }.OfferedOrDefault());

    [Fact]
    public void OfferedOrDefault_drops_codes_that_are_not_known() =>
        Assert.Equal(["de", "en"], new LanguageSettings { Offered = ["de", "xx"] }.OfferedOrDefault());

    [Fact]
    public void Settings_without_a_Languages_section_still_load()
    {
        var path = Path.Combine(_dir, "settings.json");
        new SettingsStore(path).LoadOrCreate();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("languages");
        File.WriteAllText(path, node.ToJsonString());

        var s = new SettingsStore(path).LoadOrCreate();

        Assert.Equal("en", s.Languages.Default);
        Assert.Equal(Languages.BuiltIn, s.Languages.OfferedOrDefault());
    }
}
