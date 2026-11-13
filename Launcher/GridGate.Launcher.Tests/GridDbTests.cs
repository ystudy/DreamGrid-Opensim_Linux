using GridGate.Launcher.Core.Database;
using MySqlConnector;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class GridDbTests
{
    [Fact]
    public async Task Opening_a_closed_port_throws_the_drivers_error()
    {
        var s = TestSettings.Create() with { MariaDbPort = TestPorts.Free(1)[0] };
        await Assert.ThrowsAsync<MySqlException>(() => GridDb.OpenRobustAsync(s, 2, TestContext.Current.CancellationToken));
    }
}
