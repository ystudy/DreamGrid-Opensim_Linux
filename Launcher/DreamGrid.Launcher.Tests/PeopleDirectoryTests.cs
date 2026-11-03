using DreamGrid.Launcher.Core.Database;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class PeopleDirectoryTests
{
    [Theory]
    [InlineData(null, "%")]
    [InlineData("", "%")]
    [InlineData(" ann ", "%ann%")]
    [InlineData("50%", "%50\\%%")]
    [InlineData("Ann_B", "%Ann\\_B%")]
    [InlineData("a\\b", "%a\\\\b%")]
    public void Like_pattern(string? text, string expected) => Assert.Equal(expected, PeopleDirectory.LikePattern(text));

    [Fact]
    public void Like_pattern_is_cut_to_64_characters() =>
        Assert.Equal("%" + new string('x', PeopleDirectory.MaxSearch) + "%", PeopleDirectory.LikePattern(new string('x', 70)));

    [Fact]
    public void Page_size_is_25() => Assert.Equal(25, PeopleDirectory.PageSize);
}
