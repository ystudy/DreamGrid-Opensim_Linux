using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class MoneySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-money-settings-" + Guid.NewGuid().ToString("N"));

    public MoneySettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        var m = new MoneySettings();
        Assert.Equal(MoneyMode.Off, m.Mode);
        Assert.Equal("C$", m.CurrencySymbol);
        Assert.Equal("coins", m.CurrencyName);
        Assert.Equal(100, m.StartingBalancePlay);
        Assert.Equal(0, m.StartingBalanceReal);
        Assert.Equal(0, m.WeeklyAllowance);
        Assert.Equal(0, m.UploadFee);
        Assert.Equal(0, m.ClassifiedFee);
        Assert.Equal(5000, m.DailyPayoutLimit);
        Assert.Equal(1_000_000, m.MaxPayment);
        Assert.True(m.ScriptPayouts);
        Assert.True(m.VisitorWallets);
        Assert.Empty(m.Keys);
    }

    [Theory]
    [InlineData(MoneyMode.Play)]
    [InlineData(MoneyMode.Real)]
    [InlineData(MoneyMode.Gloebit)]
    public void Unapproved_modes_read_as_off(MoneyMode mode) =>
        Assert.Equal(MoneyMode.Off, MoneyRelease.Effective(new MoneySettings(Mode: mode)));

    [Fact]
    public void Approvals_open_the_gate_one_level_at_a_time()
    {
        Assert.Equal(MoneyMode.Play, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Play), true, false));
        Assert.Equal(MoneyMode.Gloebit, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Gloebit), true, false));
        Assert.Equal(MoneyMode.Off, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Real), true, false));
        Assert.Equal(MoneyMode.Off, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Real), false, true));
        Assert.Equal(MoneyMode.Real, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Real), true, true));
    }

    [Fact]
    public void Starting_balance_follows_the_mode()
    {
        var m = new MoneySettings(StartingBalancePlay: 100, StartingBalanceReal: 7);
        Assert.Equal(100, m.StartingBalance(MoneyMode.Play));
        Assert.Equal(7, m.StartingBalance(MoneyMode.Real));
        Assert.Equal(0, m.StartingBalance(MoneyMode.Off));
        Assert.Equal(0, m.StartingBalance(MoneyMode.Gloebit));
    }

    [Fact]
    public void Schema_6_file_without_money_loads_with_defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 6,
              "db": { "rootPassword": "r", "robustPassword": "b", "opensimPassword": "o" },
              "console": { "user": "u", "password": "p" },
              "machineId": "m"
            }
            """);

        var s = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(7, s.SchemaVersion);
        Assert.Equal("", s.Db.MoneyPassword);
        Assert.Equal(MoneyMode.Off, s.Money.Mode);
        Assert.Equal(8000, s.MoneyPort);
    }

    [Fact]
    public void Money_settings_round_trip()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        var s = store.LoadOrCreate() with
        {
            Money = new MoneySettings(Mode: MoneyMode.Play, CurrencySymbol: "Z$", ProgramKeys: new() { ["a"] = "k" }),
        };
        store.Save(s);

        var back = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(MoneyMode.Play, back.Money.Mode);
        Assert.Equal("Z$", back.Money.CurrencySymbol);
        Assert.Equal("k", back.Money.Keys["a"]);
    }

    [Fact]
    public void Money_port_is_taken() => Assert.Contains(8000, PortAllocator.Taken(TestSettings.Create()));
}
