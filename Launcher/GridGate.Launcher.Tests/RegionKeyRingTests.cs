using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class RegionKeyRingTests
{
    private static readonly Guid R1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid R2 = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid R3 = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Gone = Guid.Parse("10000000-0000-0000-0000-000000000009");

    private const string Key1 = "key-for-program-one-000000000000000000000000000";
    private const string Key2 = "key-for-program-two-000000000000000000000000000";

    private static LauncherSettings TwoPrograms() => TestSettings.Create() with
    {
        Programs = [new ProgramSettings("p1", 8004, 8011), new ProgramSettings("p2", 8005, 8012)],
        Regions =
        [
            new RegionSettings("p1", "One", R1, UdpPort: 8004),
            new RegionSettings("p1", "Two", R2, UdpPort: 8006),
            new RegionSettings("p2", "Three", R3, UdpPort: 8005),
            new RegionSettings("p1", "Deleted", Gone, UdpPort: 8007, DeletedAt: DateTimeOffset.UnixEpoch),
        ],
        Money = new MoneySettings(ProgramKeys: new() { ["p1"] = Key1, ["p2"] = Key2 }),
    };

    [Fact]
    public void Right_key_maps_to_its_regions()
    {
        var ring = RegionKeyRing.FromSettings(TwoPrograms());

        Assert.True(ring.TryAuthenticate(Key1, out var grant));
        Assert.Equal("p1", grant!.KeyId);
        Assert.Equal(new HashSet<Guid> { R1, R2 }, grant.Regions.ToHashSet());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("key-for-program-one-000000000000000000000000001")]
    [InlineData("KEY-FOR-PROGRAM-ONE-000000000000000000000000000")]
    public void Wrong_or_empty_key_fails(string? presented)
    {
        var ring = RegionKeyRing.FromSettings(TwoPrograms());

        Assert.False(ring.TryAuthenticate(presented, out var grant));
        Assert.Null(grant);
    }

    [Fact]
    public void Deleted_regions_are_not_covered()
    {
        RegionKeyRing.FromSettings(TwoPrograms()).TryAuthenticate(Key1, out var grant);

        Assert.DoesNotContain(Gone, grant!.Regions);
    }

    [Fact]
    public void Ensure_adds_missing_and_drops_stale_keys()
    {
        var s = TwoPrograms() with
        {
            Programs = [new ProgramSettings("p1", 8004, 8011), new ProgramSettings("p3", 8008, 8013)],
            Money = new MoneySettings(ProgramKeys: new() { ["p1"] = Key1, ["p2"] = Key2 }),
        };

        var after = MoneyKeys.EnsureProgramKeys(s);

        Assert.Equal(Key1, after.Money.Keys["p1"]);
        Assert.False(after.Money.Keys.ContainsKey("p2"));
        Assert.Equal(48, after.Money.Keys["p3"].Length);
        Assert.Matches("^[A-Za-z0-9]{48}$", after.Money.Keys["p3"]);
        Assert.Equal(Key2, s.Money.Keys["p2"]); // the input is unchanged
    }

    [Fact]
    public void Ensure_leaves_complete_settings_alone()
    {
        var s = TwoPrograms();

        Assert.Same(s, MoneyKeys.EnsureProgramKeys(s));
    }
}
