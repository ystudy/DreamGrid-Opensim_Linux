using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

[Collection(GridCollection.Name)]
public sealed class MoneySchemaTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ensure_twice_is_harmless()
    {
        var db = await maria.FreshAsync();

        await db.EnsureSchemaAsync();
        await db.EnsureSchemaAsync();

        Assert.Equal(3, await db.Scalar("SELECT COUNT(*) FROM `money`.`accounts`"));
        Assert.Equal("1", await db.Text("SELECT value FROM `money`.`state` WHERE name = 'schema_version'"));
        Assert.Equal("0", await db.Text("SELECT value FROM `money`.`state` WHERE name = 'frozen'"));
        Assert.Equal("system:bank", await db.Text("SELECT home FROM `money`.`accounts` WHERE id = 1"));
        Assert.Equal("system:fees", await db.Text("SELECT home FROM `money`.`accounts` WHERE id = 2"));
        Assert.Equal("system:holding", await db.Text("SELECT home FROM `money`.`accounts` WHERE id = 3"));
        Assert.Equal("Bank", await db.Text("SELECT kind FROM `money`.`accounts` WHERE id = 1"));
    }

    [Fact]
    public async Task Moneyuser_can_open_the_money_database()
    {
        var db = await maria.FreshAsync();

        await using var conn = await db.OpenMoney(Ct);

        Assert.Equal("money", conn.Database);
    }

    [Fact]
    public async Task Provisioner_creates_money_only_with_a_password()
    {
        var db = await maria.FreshAsync();
        await db.Sql("DROP DATABASE `money`");

        await new DatabaseProvisioner(db.Settings with { Db = db.Settings.Db with { MoneyPassword = "" } }).EnsureAsync(Ct);
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'money'"));

        await new DatabaseProvisioner(db.Settings).EnsureAsync(Ct);
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'money'"));
    }
}
