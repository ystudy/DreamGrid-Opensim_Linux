using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Email;
using Microsoft.Extensions.Logging.Abstractions;
using SmtpServer.Protocol;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Sending through a real SMTP conversation (an in-process server), and the plain-words outcome of each kind of reply.</summary>
public sealed class MailTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RenderedEmail Email = new("Test subject", "Hello in text.", "<p>Hello in <em>HTML</em>.</p>");

    private static EmailAccount Account(int port, string fromName = "Avalon", string? replyTo = "help@avalon.example") =>
        new(Guid.NewGuid(), EmailProviders.Custom, "127.0.0.1", port, MailSecurity.None, FakeSmtp.User, "x", fromName, FakeSmtp.User,
            replyTo, null, null, false, 1, null, null);

    private static SmtpMailTransport Transport() => new(NullLogger<SmtpMailTransport>.Instance);

    [Fact]
    public async Task Sends_text_and_html_with_reply_to()
    {
        await using var smtp = new FakeSmtp();
        var outcome = await Transport().SendAsync(Account(smtp.Port), FakeSmtp.Password, "ann@example.com", Email, Ct);
        Assert.Equal(SendResult.Sent, outcome.Result);
        Assert.Equal("Signed in and sent", outcome.Plain);
        var m = Assert.Single(smtp.Received);
        Assert.Equal("Test subject", m.Subject);
        Assert.Equal("ann@example.com", m.To.Mailboxes.Single().Address);
        Assert.Equal("Avalon", m.From.Mailboxes.Single().Name);
        Assert.Equal("help@avalon.example", m.ReplyTo.Mailboxes.Single().Address);
        Assert.Contains("Hello in text.", m.TextBody);
        Assert.Contains("<em>HTML</em>", m.HtmlBody);
    }

    [Fact]
    public async Task Wrong_password_is_auth_failed()
    {
        await using var smtp = new FakeSmtp();
        var outcome = await Transport().SendAsync(Account(smtp.Port), "wrong", "ann@example.com", Email, Ct);
        Assert.Equal(SendResult.AuthFailed, outcome.Result);
        Assert.Equal("The server refused the password", outcome.Plain);
        Assert.Empty(smtp.Received);
    }

    [Fact]
    public async Task Quota_reply_is_account_full()
    {
        await using var smtp = new FakeSmtp { DataReply = new SmtpResponse(SmtpResponse.MailboxUnavailable.ReplyCode, "5.4.5 Daily user sending quota exceeded.") };
        var outcome = await Transport().SendAsync(Account(smtp.Port), FakeSmtp.Password, "ann@example.com", Email, Ct);
        Assert.Equal(SendResult.AccountFull, outcome.Result);
        Assert.Equal("The account has reached its sending limit", outcome.Plain);
    }

    [Fact]
    public async Task Other_data_refusal_is_temporary_not_full()
    {
        await using var smtp = new FakeSmtp { DataReply = new SmtpResponse(SmtpResponse.TransactionFailed.ReplyCode, "Something odd") };
        var outcome = await Transport().SendAsync(Account(smtp.Port), FakeSmtp.Password, "ann@example.com", Email, Ct);
        Assert.Equal(SendResult.Temporary, outcome.Result);
    }

    [Fact]
    public async Task Unknown_recipient_is_refused()
    {
        await using var smtp = new FakeSmtp();
        var outcome = await Transport().SendAsync(Account(smtp.Port), FakeSmtp.Password, "who@nowhere.example", Email, Ct);
        Assert.Equal(SendResult.RecipientRefused, outcome.Result);
        Assert.Equal("The server refused this address", outcome.Plain);
    }

    [Fact]
    public async Task Nothing_listening_is_temporary()
    {
        var port = TestPorts.Free(1)[0];
        var outcome = await Transport().SendAsync(Account(port), FakeSmtp.Password, "ann@example.com", Email, Ct);
        Assert.Equal(SendResult.Temporary, outcome.Result);
        Assert.Equal($"Couldn't reach 127.0.0.1 on port {port}", outcome.Plain);
    }

    [Fact]
    public async Task Crlf_in_names_cannot_add_headers()
    {
        await using var smtp = new FakeSmtp();
        var email = Email with { Subject = "Hi\r\nBcc: x@example.com" };
        var outcome = await Transport().SendAsync(Account(smtp.Port, fromName: "A\r\nBcc: y@example.com"), FakeSmtp.Password, "ann@example.com", email, Ct);
        Assert.Equal(SendResult.Sent, outcome.Result);
        var m = Assert.Single(smtp.Received);
        Assert.Empty(m.Bcc);
        Assert.DoesNotContain(m.Headers, h => h.Field.Equals("Bcc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Outcome_never_contains_password()
    {
        await using var smtp = new FakeSmtp();
        foreach (var to in new[] { "ann@example.com", "who@nowhere.example" })
        {
            var outcome = await Transport().SendAsync(Account(smtp.Port), "Secret-Pass-77", to, Email, Ct);
            Assert.DoesNotContain("Secret-Pass-77", outcome.Plain);
        }
    }
}
