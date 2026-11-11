using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Resident;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The one offered-languages rule every picker, page and email uses.</summary>
public sealed class OfferedLanguagesTests
{
    private static OfferedLanguages For(LanguageSettings l) => new(() => TestSettings.Create() with { Languages = l });

    [Fact]
    public void All_built_in_languages_are_offered_by_default_and_English_is_the_default()
    {
        var o = For(new LanguageSettings());
        Assert.Equal(Languages.BuiltIn.ToList(), o.Current().ToList());
        Assert.Equal("en", o.Default());
    }

    [Fact]
    public void Owner_order_and_default_are_kept()
    {
        var o = For(new LanguageSettings { Offered = ["fr", "de"], Default = "de" });
        Assert.Equal(["fr", "de"], o.Current().ToList());
        Assert.Equal("de", o.Default());
    }

    [Fact]
    public void A_default_with_no_culture_is_not_offered_and_English_is_used()
    {
        var o = For(new LanguageSettings { Offered = ["de"], Default = "xx" });
        Assert.Equal(["de"], o.Current().ToList());
        Assert.Equal("en", o.Default());
    }

    [Fact]
    public void A_hand_edited_default_with_a_culture_is_offered()
    {
        var o = For(new LanguageSettings { Offered = ["en"], Default = "ar" });
        Assert.Equal(["en", "ar"], o.Current().ToList());
        Assert.Equal("ar", o.Default());
    }

    [Fact]
    public void Static_and_injected_forms_agree()
    {
        var l = new LanguageSettings { Offered = ["ru", "es"], Default = "es" };
        Assert.Equal(OfferedLanguages.Of(l).ToList(), For(l).Current().ToList());
        Assert.Equal(OfferedLanguages.DefaultOf(l), For(l).Default());
    }
}
