using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The test mail server itself: a port taken between picking it and binding it must not fail the test using it.</summary>
public sealed class FakeSmtpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Port_taken_before_binding_picks_another()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            var takenPort = ((IPEndPoint)taken.LocalEndpoint).Port;
            var picks = new Queue<int>([takenPort]);
            await using var smtp = new FakeSmtp(() => picks.Count > 0 ? picks.Dequeue() : TestPorts.Free(1)[0]);
            Assert.NotEqual(takenPort, smtp.Port);

            var account = new EmailAccount(Guid.NewGuid(), EmailProviders.Custom, "127.0.0.1", smtp.Port, MailSecurity.None, FakeSmtp.User, "x", "Avalon",
                FakeSmtp.User, null, null, null, false, 1, null, null);
            var email = new RenderedEmail("Test subject", "Hello in text.", "<p>Hello.</p>");
            var outcome = await new SmtpMailTransport(NullLogger<SmtpMailTransport>.Instance).SendAsync(account, FakeSmtp.Password, "ann@example.com", email, Ct);
            Assert.Equal(SendResult.Sent, outcome.Result);
        }
        finally
        {
            taken.Stop();
        }
    }
}
