using GridGate.Launcher.Endpoints;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class RegionActionsTests
{
    [Fact]
    public void Busy_notice_says_what_is_running() =>
        Assert.Equal("Saving Home as an OAR is still running. Try again when it finishes.", RegionActions.BusyNotice("Saving Home as an OAR"));
}
