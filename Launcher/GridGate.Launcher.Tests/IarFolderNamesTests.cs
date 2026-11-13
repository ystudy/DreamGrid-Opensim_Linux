using GridGate.Launcher.Core.Iar;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The name of the folder an upload goes into (IAR plan task 6).</summary>
public sealed class IarFolderNamesTests
{
    [Fact]
    public void Imported_uses_the_utc_date() =>
        Assert.Equal("Imported 2026-11-12", IarFolderNames.Imported(new DateTimeOffset(2026, 11, 11, 22, 30, 0, TimeSpan.FromHours(-8)))); // 06:30 UTC on the 12th

    [Fact]
    public void FreeFolderName_adds_numbers()
    {
        Assert.Equal("Imported 2026-11-11", IarFolderNames.Free("Imported 2026-11-11", ["Notes", "Imported 2026-11-10"]));
        Assert.Equal("Imported 2026-11-11 (2)", IarFolderNames.Free("Imported 2026-11-11", ["Imported 2026-11-11"]));
        Assert.Equal("Imported 2026-11-11 (3)", IarFolderNames.Free("Imported 2026-11-11", ["imported 2026-11-11", "Imported 2026-11-11 (2)"]));
    }

    [Theory]
    [InlineData("Imported 2026-11-11 (2)", true)]
    [InlineData("Starter avatar_Chara", true)]
    [InlineData("Ünïcode Földer", true)]
    [InlineData("", false)]
    [InlineData("with \"quote\"", false)]
    [InlineData("a/b", false)]
    [InlineData("--merge", false)]
    public void Folder_names_on_a_console_line_are_checked(string name, bool ok) => Assert.Equal(ok, IarFolderNames.IsSafe(name));

    [Fact]
    public void Too_long_folder_name_is_refused() => Assert.False(IarFolderNames.IsSafe(new string('a', 65)));
}
