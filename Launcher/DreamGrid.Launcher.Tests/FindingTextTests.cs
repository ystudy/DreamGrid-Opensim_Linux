using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.ModelImport;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The guest-facing words for each finding code the converter writes.</summary>
public sealed class FindingTextTests
{
    /// <summary>Every code the converter knows (the design's findings table, plus texture-unsupported from the reader).</summary>
    public static readonly string[] Codes =
    [
        "split-materials", "split-vertices", "texture-resized", "texture-shrunk", "texture-unsupported", "heavy-mesh", "too-big",
        "too-small", "too-many-parts", "inside-out", "no-uv", "dropped-animation", "dropped-morphs", "dropped-extras", "skin-ignored",
        "compressed", "external-files", "not-triangles", "bad-file", "too-large-file",
    ];

    public static TheoryData<string> AllCodes() => new(Codes);

    private static Finding F(string code, params (string Key, string Value)[] values) =>
        new(code, FindingKind.Safe, values.ToDictionary(v => v.Key, v => v.Value));

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Every_code_has_text(string code)
    {
        var line = FindingText.Of(F(code), "Castle");
        var generic = FindingText.Of(F("no-such-code"), "Castle");
        Assert.NotEqual(generic.Title, line.Title);
        Assert.False(string.IsNullOrWhiteSpace(line.Title));
        Assert.False(string.IsNullOrWhiteSpace(line.Why));
        Assert.False(string.IsNullOrWhiteSpace(line.Tip));
        Assert.Contains("Blender", line.Tip);
    }

    [Fact]
    public void Unknown_code_gives_a_generic_line()
    {
        var line = FindingText.Of(new Finding("brand-new", FindingKind.Risky, new Dictionary<string, string>()), "Castle");
        Assert.False(string.IsNullOrWhiteSpace(line.Title));
        Assert.False(string.IsNullOrWhiteSpace(line.Tip));
    }

    [Fact]
    public void A_finding_with_no_values_dictionary_does_not_throw()
    {
        var line = FindingText.Of(new Finding("too-big", FindingKind.Blocking, null!), "Castle");
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
        var line = FindingText.Of(new Finding("too-big", FindingKind.Blocking, values), "Castle");
        Assert.Equal("Tower is too big", line.Title);
    }

    [Fact]
    public void Values_are_put_into_the_words()
    {
        var split = FindingText.Of(F("split-materials", ("materials", "20"), ("perPart", "8"), ("parts", "3")), "Castle");
        Assert.Equal("Split into 3 linked parts (20 materials, 8 per part). This won't change how it looks.", split.Title + " " + split.Why);

        var big = FindingText.Of(F("too-big", ("part", "Tower"), ("side", "300"), ("limit", "256"), ("fitPercent", "85")), "Castle");
        Assert.Contains("Tower", big.Title);
        Assert.Contains("300 m", big.Title);
        Assert.Contains("256 m", big.Why);

        var heavy = FindingText.Of(F("heavy-mesh", ("triangles", "2100000"), ("threshold", "200000")), "Castle");
        Assert.Contains("2,100,000", heavy.Title);
        Assert.Contains("200,000", heavy.Why);

        var upload = FindingText.Of(F("too-large-file", ("what", "upload"), ("limit", "67108864")), "Castle");
        Assert.Contains("64 MB", upload.Title + upload.Why);
    }
}
