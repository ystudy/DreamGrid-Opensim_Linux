using DreamGrid.Launcher.Core.Security;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class PasswordRulesTests
{
    [Fact]
    public void A_good_password_has_no_problem() =>
        Assert.Null(PasswordRules.Problem("tall-green-kettle", "tall-green-kettle", "Alexandra", "Northwood"));

    [Fact]
    public void Seven_letters_is_too_short() =>
        Assert.Equal("Use at least 8 characters.", PasswordRules.Problem("abcdefg", "abcdefg", "Ann", "Smith"));

    [Fact]
    public void Missing_password_is_too_short() =>
        Assert.Equal("Use at least 8 characters.", PasswordRules.Problem(null, null, "Ann", "Smith"));

    [Theory]
    [InlineData("Password1")]
    [InlineData("QWERTY123")]
    [InlineData("password")]
    [InlineData("iloveyou")]
    public void Common_password_refused_any_case(string password) =>
        Assert.Equal("That password is too common. Choose another.", PasswordRules.Problem(password, password, "Ann", "Smith"));

    [Theory]
    [InlineData("alexandra")]
    [InlineData("NORTHWOOD")]
    [InlineData("Alexandra Northwood")]
    public void Name_as_password_refused(string password) =>
        Assert.Equal("Don't use your avatar's name as the password.", PasswordRules.Problem(password, password, "Alexandra", "Northwood"));

    [Fact]
    public void Name_inside_a_longer_password_is_allowed() =>
        Assert.Null(PasswordRules.Problem("northwood7x", "northwood7x", "Alexandra", "Northwood"));

    [Fact]
    public void Mismatch_reported_first() =>
        Assert.Equal("Type the same password twice.", PasswordRules.Problem("short", "other", "Ann", "Smith"));
}
