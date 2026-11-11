using System.Globalization;
using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.ModelImport;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The guest-facing words for each finding code the converter writes.</summary>
public sealed class FindingTextTests
{
    /// <summary>The resident text as the pages read it, without owner wording.</summary>
    private static readonly IStringLocalizer<ResidentText> Text = new StringLocalizer<ResidentText>(
        new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }), NullLoggerFactory.Instance));

    /// <summary>The finding's words in <paramref name="language"/>, as a page in that language reads them (the culture is put back after).</summary>
    private static FindingLine Of(Finding f, string itemName, string language = "en")
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        try { return FindingText.Of(Text, f, itemName); }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, ui); }
    }

    /// <summary>Every code the converter knows (the design's findings table, plus texture-unsupported from the reader).</summary>
    public static readonly string[] Codes =
    [
        "split-materials", "split-vertices", "texture-resized", "texture-shrunk", "texture-unsupported", "heavy-mesh", "too-big",
        "too-small", "too-many-parts", "inside-out", "no-uv", "dropped-animation", "dropped-morphs", "dropped-extras", "skin-ignored",
        "compressed", "external-files", "not-triangles", "bad-file", "too-large-file", "vertex-colours-ignored", "double-sided-ignored",
        "texture-coords-ignored",
    ];

    public static TheoryData<string> AllCodes() => new(Codes);

    private static Finding F(string code, params (string Key, string Value)[] values) =>
        new(code, FindingKind.Safe, values.ToDictionary(v => v.Key, v => v.Value));

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Every_code_has_text(string code)
    {
        var line = Of(F(code), "Castle");
        var generic = Of(F("no-such-code"), "Castle");
        Assert.NotEqual(generic.Title, line.Title);
        Assert.False(string.IsNullOrWhiteSpace(line.Title));
        Assert.False(string.IsNullOrWhiteSpace(line.Why));
        Assert.False(string.IsNullOrWhiteSpace(line.Tip));
        Assert.Contains("Blender", line.Tip);
    }

    [Fact]
    public void Unknown_code_gives_a_generic_line()
    {
        var line = Of(new Finding("brand-new", FindingKind.Risky, new Dictionary<string, string>()), "Castle");
        Assert.False(string.IsNullOrWhiteSpace(line.Title));
        Assert.False(string.IsNullOrWhiteSpace(line.Tip));
    }

    [Fact]
    public void A_finding_with_no_values_dictionary_does_not_throw()
    {
        var line = Of(new Finding("too-big", FindingKind.Blocking, null!), "Castle");
        Assert.Contains("Castle", line.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Too_big_without_a_side_uses_the_plain_words(string? side)
    {
        var values = new Dictionary<string, string> { ["part"] = "Tower", ["limit"] = "256" };
        if (side is not null) values["side"] = side;
        var line = Of(new Finding("too-big", FindingKind.Blocking, values), "Castle");
        Assert.Equal("Tower is too big", line.Title);
    }

    [Fact]
    public void Values_are_put_into_the_words()
    {
        var split = Of(F("split-materials", ("materials", "20"), ("perPart", "8"), ("parts", "3")), "Castle");
        Assert.Equal("Split into 3 linked parts (20 materials, 8 per part). This won't change how it looks.", split.Title + " " + split.Why);

        var big = Of(F("too-big", ("part", "Tower"), ("side", "300"), ("limit", "256"), ("fitPercent", "85")), "Castle");
        Assert.Contains("Tower", big.Title);
        Assert.Contains("300 m", big.Title);
        Assert.Contains("256 m", big.Why);

        var heavy = Of(F("heavy-mesh", ("triangles", "2100000"), ("threshold", "200000")), "Castle");
        Assert.Contains("2,100,000", heavy.Title);
        Assert.Contains("200,000", heavy.Why);

        var upload = Of(F("too-large-file", ("what", "upload"), ("limit", "67108864")), "Castle");
        Assert.Contains("64 MB", upload.Title + upload.Why);

        var assets = Of(F("too-large-file", ("what", "assets"), ("limit", "67108864")), "Castle");
        Assert.NotEqual(upload.Title, assets.Title);
        Assert.Contains("64 MB", assets.Title + assets.Why);
        Assert.Contains("Blender", assets.Tip);

        var sides = Of(F("double-sided-ignored", ("material", "Leaves"), ("materials", "2")), "Castle");
        Assert.Contains("Leaves", sides.Title + sides.Why);
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Every_code_has_text_in_every_built_in_language(string code)
    {
        foreach (var language in Core.Languages.BuiltIn.Where(l => l != Core.Languages.English))
        {
            var english = Of(F(code), "Castle");
            var line = Of(F(code), "Castle", language);
            Assert.NotEqual(english.Title, line.Title);
            Assert.False(string.IsNullOrWhiteSpace(line.Why), language);
            Assert.Contains("Blender", line.Tip);
        }
    }

    [Fact]
    public void Numbers_are_written_in_the_page_language()
    {
        var heavy = Of(F("heavy-mesh", ("triangles", "2100000"), ("threshold", "200000")), "Castle", "de");
        Assert.Contains("2.100.000", heavy.Title);
        var small = Of(F("too-small", ("dropped", "2"), ("total", "9"), ("limit", "0.001")), "Castle", "de");
        Assert.Contains("0,001", small.Why);
    }

    [Fact]
    public void Missing_numbers_and_names_become_words_not_placeholders()
    {
        foreach (var language in Core.Languages.BuiltIn)
            foreach (var code in Codes)
            {
                var line = Of(F(code), "", language);
                Assert.DoesNotContain("{", line.Title + line.Why + line.Tip);
            }
    }

    [Fact]
    public void Lists_of_left_out_things_are_joined_in_the_language()
    {
        Assert.Equal("Cameras and lights were left out", Of(F("dropped-extras", ("what", "cameras,lights")), "Castle").Title);
        Assert.Equal("Kameras und Lichter wurden weggelassen", Of(F("dropped-extras", ("what", "cameras,lights")), "Castle", "de").Title);
    }
}
