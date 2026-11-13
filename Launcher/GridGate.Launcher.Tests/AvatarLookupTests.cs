using System.Collections.Concurrent;
using GridGate.Launcher.Estates;
using GridGate.Launcher.Security;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class AvatarLookupTests
{
    [Fact]
    public async Task A_lookup_that_gets_no_answer_says_so_under_the_callers_name()
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        var log = new CapturingLoggerProvider(logs).CreateLogger("test");
        var (account, problem) = await AvatarLookup.FindAsync(new FakeRobustAccounts { Down = true }, "Ann Smith", log,
            TestContext.Current.CancellationToken, "Groups");
        Assert.Null(account);
        Assert.Equal(StaffActions.RobustDown, problem);
        Assert.Equal("Groups: the account service did not answer: Connection refused", Assert.Single(logs).Message);
    }

    [Fact]
    public async Task Typing_one_word_asks_for_both_names()
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        var log = new CapturingLoggerProvider(logs).CreateLogger("test");
        var (account, problem) = await AvatarLookup.FindAsync(new FakeRobustAccounts(), "Ann", log, TestContext.Current.CancellationToken);
        Assert.Null(account);
        Assert.Equal("Type the avatar's first and last name.", problem);
    }
}
