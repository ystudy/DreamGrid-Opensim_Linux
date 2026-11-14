using System.Net;
using System.Security.Cryptography.X509Certificates;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Money;

/// <summary>
/// The region-facing money service (money design §7.2): its own Kestrel host inside the launcher, on its own HTTPS port,
/// listening on 127.0.0.1 only, with nothing but the money endpoints. It doesn't start while the effective mode is Off.
/// It has no logging providers of its own, so Kestrel writes no request lines; the endpoints log through the launcher's.
/// </summary>
public sealed class MoneyServiceHost(
    MoneyLedger ledger,
    PresenceCheck presence,
    Func<LauncherSettings> settings,
    Func<MoneyMode> effective,
    Func<X509Certificate2> certificate,
    MoneyAuthFailures failures,
    ILoggerFactory loggers) : IHostedService
{
    /// <summary>A request body larger than this is refused before it is read.</summary>
    public const long MaxBodyBytes = 16 * 1024;

    private readonly ILogger _log = loggers.CreateLogger<MoneyServiceHost>();
    private WebApplication? _app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (effective() == MoneyMode.Off) return;
        var port = settings().MoneyPort;
        var cert = certificate();

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = MaxBodyBytes;
            o.Listen(IPAddress.Loopback, port, l => l.UseHttps(cert));
        });
        builder.Services.AddRoutingCore();
        var app = builder.Build();
        app.UseRouting();
        MoneyApi.Map(app, new MoneyApi.Context(ledger, presence, settings, failures, _log));

        await app.StartAsync(cancellationToken);
        _app = app;
        _log.LogInformation("The money service is listening on 127.0.0.1:{Port}.", port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null) return;
        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
    }
}
