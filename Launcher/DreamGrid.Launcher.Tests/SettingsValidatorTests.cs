using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class SettingsValidatorTests
{
    private static Core.Modules.ValidationResult Valid(string? hostname = null, int web = 8001, int region = 8004, int mariaDb = 3307) =>
        SettingsValidator.Network(hostname, web, 8002, 8003, region, mariaDb, 8010, 8011);

    [Theory]
    [InlineData(100, 200, true)]
    [InlineData(1, 2, true)]
    [InlineData(100, 255, true)]
    [InlineData(0, 200, false)]
    [InlineData(100, 100, false)]
    [InlineData(150, 100, false)]
    [InlineData(100, 256, false)]
    public void Security_levels_are_checked(int supervisor, int admin, bool ok) =>
        Assert.Equal(ok, SettingsValidator.Security(new SecuritySettings(supervisor, admin)).Ok);

    [Fact]
    public void Validator_accepts_the_defaults()
    {
        Assert.True(Valid().Ok);
        Assert.True(Valid("").Ok);
    }

    [Fact]
    public void Validator_rejects_out_of_range_duplicate_ports_and_bad_hostname()
    {
        var zero = Valid(web: 0);
        Assert.Equal(["WebPort"], zero.Errors.Keys);

        var high = Valid(mariaDb: 70000);
        Assert.Equal(["MariaDbPort"], high.Errors.Keys);

        var low = Valid(region: 1023);
        Assert.Equal(["RegionPort"], low.Errors.Keys);

        var duplicate = Valid(web: 8004);
        Assert.Equal(["RegionPort", "WebPort"], duplicate.Errors.Keys.Order());
        Assert.Contains("8004", duplicate.Errors["WebPort"]);

        var host = Valid("my host");
        Assert.Equal(["Hostname"], host.Errors.Keys);
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("world.example.com")]
    [InlineData("my-pc")]
    [InlineData("a1.b-2.c3")]
    public void Validator_accepts_dns_names_and_ipv4(string hostname) => Assert.True(Valid(hostname).Ok);

    [Theory]
    [InlineData(" world.example.com")]
    [InlineData("world.example.com ")]
    [InlineData("tab\there")]
    [InlineData("-leading.example.com")]
    [InlineData("trailing-.example.com")]
    [InlineData("double..dot")]
    [InlineData("under_score")]
    [InlineData("192.168.1.300")]
    [InlineData("1.2.3")]
    [InlineData("http://host")]
    [InlineData("host:8001")]
    public void Validator_rejects_bad_hostnames(string hostname) =>
        Assert.True(Valid(hostname).Errors.ContainsKey("Hostname"), hostname);

    [Fact]
    public void Validator_rejects_hostnames_over_253_characters()
    {
        var label = new string('a', 60);
        var name = string.Join('.', Enumerable.Repeat(label, 5)); // 304 characters

        Assert.True(Valid(name).Errors.ContainsKey("Hostname"));
        Assert.True(Valid(new string('a', 64) + ".com").Errors.ContainsKey("Hostname"));
    }

    [Theory]
    [InlineData("My \"World\"")]
    [InlineData("Line\nbreak")]
    [InlineData("Tab\there")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Dream; World")]
    [InlineData("Dream # World")]
    [InlineData("A ${Const|PublicPort}")]
    [InlineData("[World]")]
    [InlineData("a{b}")]
    [InlineData("a=b")]
    [InlineData("a\\b")]
    public void Grid_name_with_quote_newline_or_ini_symbol_is_rejected(string? name) =>
        Assert.Equal("Use 1 to 64 letters, digits, spaces or punctuation, without quotes or the symbols ; # $ [ ] { } = \\",
            SettingsValidator.GridNameError(name));

    [Fact]
    public void Grid_name_length_is_one_to_sixty_four_after_trimming()
    {
        Assert.Null(SettingsValidator.GridNameError("A"));
        Assert.Null(SettingsValidator.GridNameError("  " + new string('a', 64) + "  "));
        Assert.NotNull(SettingsValidator.GridNameError(new string('a', 65)));
        Assert.Null(SettingsValidator.GridNameError("Dream's World - v2!"));
    }
}
