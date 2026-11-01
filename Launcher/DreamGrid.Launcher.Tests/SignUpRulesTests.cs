using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Sign-up's name and email rules and when sign-up is open (spec 2026-10-31-sign-up-design §3, §4). All names are made up.</summary>
public sealed class SignUpRulesTests
{
    private static readonly DateTimeOffset Saved = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private static LegalSettings Gate(bool open) => new(
        new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = new[] { "terms", "privacy", "cookies" }.ToDictionary(p => p, p => new LegalText($"Test {p}.", 1, Saved)),
        },
        open,
        open ? Saved : null);

    private static LauncherSettings Settings(SignUpSettings? signUp = null, bool gate = true) => new()
    {
        Db = new("a", "b", "c"), Console = new("u", "p"), MachineId = "m", Legal = Gate(gate), SignUp = signUp,
        Setup = new() { Owner = new OwnerInfo("Gwen", "Holloway", Guid.NewGuid()) },
    };

    [Fact]
    public void Null_settings_is_closed() => Assert.False(SignUpRules.IsOpen(Settings(signUp: null)));

    [Fact]
    public void Open_needs_the_legal_gate()
    {
        Assert.True(SignUpRules.IsOpen(Settings(new SignUpSettings(Open: true))));
        Assert.False(SignUpRules.IsOpen(Settings(new SignUpSettings(Open: true), gate: false)));
        Assert.False(SignUpRules.IsOpen(Settings(new SignUpSettings(Open: false))));
    }

    [Fact]
    public void A_good_name_has_no_problem() => Assert.Null(SignUpRules.NameProblem("Ann", "Example", Settings()));

    [Fact]
    public void Name_32_chars_refused()
    {
        Assert.NotNull(SignUpRules.NameProblem(new string('a', 32), "Example", Settings()));
        Assert.NotNull(SignUpRules.NameProblem("Ann", new string('b', 32), Settings()));
        Assert.Null(SignUpRules.NameProblem(new string('a', 31), new string('b', 31), Settings()));
    }

    [Theory]
    [InlineData("Ann Marie", "Example")]
    [InlineData("Ann.", "Example")]
    [InlineData("Ann", "Ex@mple")]
    [InlineData("", "Example")]
    [InlineData("Ann", null)]
    [InlineData("Ännä", "Example")]
    public void Name_with_space_or_dot_refused(string? first, string? last) =>
        Assert.NotNull(SignUpRules.NameProblem(first, last, Settings()));

    [Fact]
    public void Owner_name_refused_any_case() => Assert.NotNull(SignUpRules.NameProblem("gwen", "HOLLOWAY", Settings()));

    [Theory]
    [InlineData("Test", "User")]
    [InlineData("grid", "owner")]
    [InlineData("Admin", "Admin")]
    public void Reserved_name_refused(string first, string last) => Assert.NotNull(SignUpRules.NameProblem(first, last, Settings()));

    [Theory]
    [InlineData("Ann-Marie", "Example")]
    [InlineData("Bot7", "Example")]
    public void Hyphen_and_digits_allowed(string first, string last) => Assert.Null(SignUpRules.NameProblem(first, last, Settings()));

    [Theory]
    [InlineData("ann@@x.com")]
    [InlineData("a@b@x.com")]
    [InlineData("@x.com")]
    [InlineData("ann@")]
    [InlineData("ann x@x.com")]
    [InlineData("ann\"@x.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Email_two_ats_refused(string? email) => Assert.NotNull(SignUpRules.EmailProblem(email));

    [Fact]
    public void Email_longer_than_robust_keeps_refused()
    {
        Assert.Null(SignUpRules.EmailProblem(new string('a', 54) + "@example.x"));
        Assert.NotNull(SignUpRules.EmailProblem(new string('a', 55) + "@example.x"));
    }

    [Fact]
    public void Email_starting_with_two_dashes_refused() => Assert.NotNull(SignUpRules.EmailProblem("--ann@example.com"));

    [Fact]
    public void A_good_email_has_no_problem() => Assert.Null(SignUpRules.EmailProblem(" Ann@X.com "));

    [Fact]
    public void Email_trimmed_and_lowered() => Assert.Equal("ann@x.com", SignUpRules.NormalEmail(" Ann@X.com "));
}
