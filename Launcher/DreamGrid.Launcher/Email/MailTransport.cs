using System.Net.Sockets;
using DreamGrid.Launcher.Core.Email;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace DreamGrid.Launcher.Email;

/// <summary>What became of one email, in the terms the outbox acts on.</summary>
public enum SendResult
{
    Sent,

    /// <summary>The server refused the username or password: the account needs the owner.</summary>
    AuthFailed,

    /// <summary>The account reached its provider's sending limit: try another account until the window frees.</summary>
    AccountFull,

    /// <summary>Couldn't connect, timed out, or a temporary refusal: try another account, retry this one later.</summary>
    Temporary,

    /// <summary>The server refused the recipient for good: the email fails, on every account.</summary>
    RecipientRefused,
}

/// <summary>
/// The outcome and the sentence the owner sees, for example "The server refused the password". <see cref="Plain"/> never contains
/// the password or the message.
/// </summary>
public sealed record SendOutcome(SendResult Result, string Plain);

/// <summary>Sends one email through one account. The seam the outbox is tested through.</summary>
public interface IMailTransport
{
    Task<SendOutcome> SendAsync(EmailAccount account, string password, string to, RenderedEmail email, CancellationToken ct);
}

/// <summary>
/// SMTP with MailKit: the account's own security setting (never "auto", so a named provider is never talked down to plain
/// text; certificates are checked), a 30-second timeout, and a multipart text-and-HTML message. Names and the subject are put in
/// through MimeKit's types with line breaks taken out, so nothing can add a header.
/// </summary>
public sealed class SmtpMailTransport(ILogger<SmtpMailTransport> log) : IMailTransport
{
    private static readonly string[] FullWords = ["quota", "limit", "rate", "too many", "5.4.5", "4.7.0"];

    public async Task<SendOutcome> SendAsync(EmailAccount account, string password, string to, RenderedEmail email, CancellationToken ct)
    {
        if (!MailboxAddress.TryParse(to, out var recipient))
            return new(SendResult.RecipientRefused, "The address isn't a valid email address");
        if (!MailboxAddress.TryParse(account.FromAddress, out var fromAddress))
            return new(SendResult.AuthFailed, "The \"From\" address isn't a valid email address");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(OneLine(account.FromName), fromAddress.Address));
        if (!string.IsNullOrWhiteSpace(account.ReplyTo) && MailboxAddress.TryParse(account.ReplyTo, out var replyTo))
            message.ReplyTo.Add(new MailboxAddress(null, replyTo.Address));
        message.To.Add(new MailboxAddress(null, recipient.Address));
        message.Subject = OneLine(email.Subject);
        message.Body = new BodyBuilder { TextBody = email.Text, HtmlBody = email.Html }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        try
        {
            await client.ConnectAsync(account.Host, account.Port, Options(account.Security), ct);
            if (client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                await client.AuthenticateAsync(account.UserName, password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return new(SendResult.Sent, "Signed in and sent");
        }
        catch (AuthenticationException)
        {
            return new(SendResult.AuthFailed, "The server refused the password");
        }
        catch (SmtpCommandException e) when (e.ErrorCode == SmtpErrorCode.RecipientNotAccepted && (int)e.StatusCode >= 500)
        {
            return new(SendResult.RecipientRefused, "The server refused this address");
        }
        catch (SmtpCommandException e) when (IsFull(e))
        {
            return new(SendResult.AccountFull, "The account has reached its sending limit");
        }
        catch (SmtpCommandException e) when (e.StatusCode == SmtpStatusCode.AuthenticationRequired || e.ErrorCode == SmtpErrorCode.SenderNotAccepted)
        {
            return new(SendResult.AuthFailed, "The server didn't accept this account as the sender");
        }
        catch (SmtpCommandException e)
        {
            log.LogInformation("{Host} refused an email: {Code}", account.Host, (int)e.StatusCode);
            return new(SendResult.Temporary, $"The server refused the email for now (code {(int)e.StatusCode})");
        }
        catch (SslHandshakeException)
        {
            return new(SendResult.Temporary, $"Couldn't start a secure connection with {account.Host}");
        }
        catch (Exception e) when (e is SocketException or IOException or SmtpProtocolException or ServiceNotConnectedException)
        {
            return new(SendResult.Temporary, $"Couldn't reach {account.Host} on port {account.Port}");
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new(SendResult.Temporary, $"{account.Host} didn't answer in time");
        }
    }

    private static SecureSocketOptions Options(MailSecurity security) => security switch
    {
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.None,
    };

    /// <summary>A quota or rate reply: a 4xx or 5xx whose text says so (Gmail: 550 5.4.5, 421 4.7.0).</summary>
    private static bool IsFull(SmtpCommandException e) =>
        (int)e.StatusCode is 421 or 450 or 451 or 452 or 454 or 550 or 552 or 554
        && FullWords.Any(w => e.Message.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static string OneLine(string? text) => (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
}
