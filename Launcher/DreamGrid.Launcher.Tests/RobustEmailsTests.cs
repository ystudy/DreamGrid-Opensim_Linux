using DreamGrid.Launcher.Core.OpenSim;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RobustEmailsTests
{
    [Fact]
    public void Update_statement_targets_one_principal()
    {
        Assert.Contains("SET Email = @email", RobustEmails.Update);
        Assert.Contains("WHERE PrincipalID = @id", RobustEmails.Update);
    }

    [Fact]
    public async Task Unreachable_database_throws_unavailable()
    {
        // Nothing listens on this port, so the connection is refused at once.
        var s = TestSettings.Create() with { MariaDbPort = TestPorts.Free(1)[0] };
        var emails = new RobustEmails(() => s);
        await Assert.ThrowsAsync<RobustEmailsUnavailableException>(() =>
            emails.SetAsync(Guid.NewGuid(), "ann@example.com", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fake_writes_the_new_email()
    {
        var robust = new FakeRobustAccounts().Add("Ann", "Smith", 0, "password", email: "old@example.com");
        var ann = robust.All.Single();
        await ((IRobustEmails)robust).SetAsync(ann.PrincipalId, "new@example.com", CancellationToken.None);
        Assert.Equal("new@example.com", robust.All.Single().Email);
    }

    [Fact]
    public async Task Fake_emails_can_be_down()
    {
        var robust = new FakeRobustAccounts().Add("Ann", "Smith", 0, "password");
        robust.EmailsDown = true;
        await Assert.ThrowsAsync<RobustEmailsUnavailableException>(() =>
            ((IRobustEmails)robust).SetAsync(robust.All.Single().PrincipalId, "new@example.com", CancellationToken.None));
    }
}
