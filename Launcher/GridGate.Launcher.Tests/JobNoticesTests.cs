using GridGate.Launcher.Core.Grid;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class JobNoticesTests
{
    [Fact]
    public void Busy_notice_says_what_is_running() =>
        Assert.Equal("Saving Home as an OAR is still running. Try again when it finishes.", JobNotices.Busy("Saving Home as an OAR"));
}
