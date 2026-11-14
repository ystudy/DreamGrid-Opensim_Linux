using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Checking a session against Robust's Presence table (money plan 1, task 7).</summary>
[Collection(GridCollection.Name)]
public sealed class PresenceCheckTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid User = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Here = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private static readonly Guid There = Guid.Parse("f0000000-0000-0000-0000-00000000000f");
    private static readonly Guid Session = Guid.Parse("50000000-0000-0000-0000-000000000005");
    private static readonly Guid Secure = Guid.Parse("60000000-0000-0000-0000-000000000006");
    private static readonly IReadOnlySet<Guid> Mine = new HashSet<Guid> { Here };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<PresenceCheck> Setup(Guid region)
    {
        var db = await maria.FreshAsync();
        await db.AddPresence(User, region, Session, Secure);
        await db.AddUser(User);
        return new PresenceCheck(db.OpenRobust);
    }

    [Fact]
    public async Task Matching_session_in_region_is_Ok() =>
        Assert.Equal(PresenceOutcome.Ok, await (await Setup(Here)).CheckAsync(User, Session, Secure, Mine, Ct));

    [Fact]
    public async Task Session_in_another_region_is_WrongRegion() =>
        Assert.Equal(PresenceOutcome.WrongRegion, await (await Setup(There)).CheckAsync(User, Session, Secure, Mine, Ct));

    [Fact]
    public async Task Wrong_secure_session_is_NoSession() =>
        Assert.Equal(PresenceOutcome.NoSession, await (await Setup(Here)).CheckAsync(User, Session, Guid.NewGuid(), Mine, Ct));

    [Fact]
    public async Task Someone_elses_session_is_NoSession() =>
        Assert.Equal(PresenceOutcome.NoSession, await (await Setup(Here)).CheckAsync(Guid.NewGuid(), Session, Secure, Mine, Ct));

    [Fact]
    public async Task Zero_session_is_NoSession()
    {
        var check = await Setup(Here);

        Assert.Equal(PresenceOutcome.NoSession, await check.CheckAsync(User, Guid.Empty, Secure, Mine, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.CheckAsync(User, Session, Guid.Empty, Mine, Ct));
    }

    [Fact]
    public async Task Visitor_presence_row_counts()
    {
        var db = await maria.FreshAsync();
        var visitor = Guid.NewGuid();
        await db.AddPresence(visitor, Here, Session, Secure); // the Gatekeeper's LoginAgent row: no UserAccounts row
        var check = new PresenceCheck(db.OpenRobust);

        Assert.Equal(PresenceOutcome.Ok, await check.CheckAsync(visitor, Session, Secure, Mine, Ct));
        Assert.False(await check.IsLocalUserAsync(visitor, Ct));
    }

    [Fact]
    public async Task IsLocalUser_reads_UserAccounts()
    {
        var check = await Setup(Here);

        Assert.True(await check.IsLocalUserAsync(User, Ct));
        Assert.False(await check.IsLocalUserAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Unreachable_robust_is_Unavailable()
    {
        var db = await maria.FreshAsync();
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var check = new PresenceCheck(ct => Core.Database.GridDb.OpenRobustAsync(closed, 2, ct));

        Assert.Equal(PresenceOutcome.Unavailable, await check.CheckAsync(User, Session, Secure, Mine, Ct));
    }
}
