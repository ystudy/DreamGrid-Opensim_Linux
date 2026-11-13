using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Groups;
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

    [Fact]
    public async Task A_database_failure_becomes_the_callers_exception()
    {
        var e = await Assert.ThrowsAsync<GroupsUnavailableException>(() => GridDb.RunAsync<int>(
            () => throw new TimeoutException("slow"), _ => Task.FromResult(1), x => new GroupsUnavailableException("The database could not be read.", x)));
        Assert.IsType<TimeoutException>(e.InnerException);
    }

    [Fact]
    public async Task Any_other_failure_passes_through() =>
        await Assert.ThrowsAsync<ArgumentException>(() => GridDb.RunAsync<int>(
            () => throw new ArgumentException("no"), _ => Task.FromResult(1), x => new GroupsUnavailableException("x", x)));
}
