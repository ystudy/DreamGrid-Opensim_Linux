using System.Buffers;
using MimeKit;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// A mail server on a free loopback port for transport tests (plain text, AUTH required). It accepts <see cref="User"/> /
/// <see cref="Password"/>, refuses any recipient at <c>@nowhere.example</c>, and answers the message with
/// <see cref="DataReply"/> when that is set (a quota reply, say). Received messages are in <see cref="Received"/>.
/// </summary>
internal sealed class FakeSmtp : IAsyncDisposable
{
    public const string User = "grid@example.com";
    public const string Password = "app-password-1";

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _running;

    public FakeSmtp()
    {
        Port = TestPorts.Free(1)[0];
        var options = new SmtpServerOptionsBuilder()
            .ServerName("localhost")
            .Endpoint(b => b.Endpoint(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, Port)).AllowUnsecureAuthentication(true).AuthenticationRequired(true))
            .Build();
        var services = new SmtpServer.ComponentModel.ServiceProvider();
        services.Add(new Store(this));
        services.Add(new Auth());
        services.Add(new Filter());
        _running = new SmtpServer.SmtpServer(options, services).StartAsync(_stop.Token);
    }

    public int Port { get; }

    public List<MimeMessage> Received { get; } = [];

    public SmtpResponse? DataReply { get; set; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _running; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    private sealed class Store(FakeSmtp owner) : IMessageStore
    {
        public async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken ct)
        {
            if (owner.DataReply is { } reply) return reply;
            using var stream = new MemoryStream(buffer.ToArray());
            var message = await MimeMessage.LoadAsync(stream, ct);
            lock (owner.Received) owner.Received.Add(message);
            return SmtpResponse.Ok;
        }
    }

    private sealed class Auth : IUserAuthenticator
    {
        public Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken ct) =>
            Task.FromResult(user == User && password == Password);
    }

    private sealed class Filter : IMailboxFilter
    {
        public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken ct) =>
            Task.FromResult(!string.Equals(to.Host, "nowhere.example", StringComparison.OrdinalIgnoreCase));
    }
}
