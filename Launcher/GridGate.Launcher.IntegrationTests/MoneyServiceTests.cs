using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>The region-facing money service over HTTPS (money plan 1, task 9).</summary>
[Collection(GridCollection.Name)]
public sealed class MoneyServiceTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Here = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private static readonly Guid There = Guid.Parse("f0000000-0000-0000-0000-00000000000f");
    private static readonly Guid SessionA = Guid.Parse("5000000a-0000-0000-0000-000000000005");
    private static readonly Guid SecureA = Guid.Parse("6000000a-0000-0000-0000-000000000006");
    private const string Key = "the-region-key-for-program-one-0000000000000000";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-money-svc-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _logs = new();
    private MoneyServiceHost? _host;
    private HttpClient? _http;
    private string _fingerprint = "";
    private int _port;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_host is not null) await _host.StopAsync(CancellationToken.None);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Starts the service on a fresh database: program p1 holds region Here, and A is logged in there.</summary>
    private async Task<MoneyTestDatabase> StartAsync(MoneyMode mode = MoneyMode.Play, Func<LauncherSettings, LauncherSettings>? dbOverride = null)
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await db.AddUser(A);
        await db.AddUser(B);
        await db.AddPresence(A, Here, SessionA, SecureA);
        _port = MoneyMariaDb.FreePort();
        var settings = db.Settings with
        {
            MoneyPort = _port,
            Programs = [new ProgramSettings("p1", 8004, 8011)],
            Regions = [new RegionSettings("p1", "Here", Here, UdpPort: 8004)],
            Money = new MoneySettings(Mode: mode, UploadFee: 3, ProgramKeys: new() { ["p1"] = Key }),
        };
        var dbSettings = dbOverride?.Invoke(settings) ?? settings;
        var time = TimeProvider.System;
        var presence = new PresenceCheck(MoneyConnections.Robust(() => dbSettings));
        var ledger = new MoneyLedger(MoneyConnections.Money(() => dbSettings), () => settings.Money, () => mode, presence.IsLocalUserAsync, time);
        var loggers = LoggerFactory.Create(b => b.AddProvider(new QueueLoggerProvider(_logs)).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_dir);
        var cert = MoneyCertificate.LoadOrCreate(Path.Combine(_dir, MoneyCertificate.FileName), time);
        _fingerprint = MoneyCertificate.Fingerprint(cert);
        _host = new MoneyServiceHost(ledger, presence, () => settings, () => mode, () => cert, new MoneyAuthFailures(time, loggers.CreateLogger<MoneyAuthFailures>()), loggers);
        await _host.StartAsync(Ct);
        _http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, c, _, _) => c is not null && MoneyCertificate.Fingerprint(X509CertificateLoader.LoadCertificate(c.GetRawCertData())) == _fingerprint },
        }) { BaseAddress = new Uri($"https://127.0.0.1:{_port}/money/v1/") };
        return db;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body, string Raw)> Post(string path, object? body, string? key = Key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json"),
        };
        if (key is not null) req.Headers.TryAddWithoutValidation("Authorization", "GridGateRegion " + key);
        using var resp = await _http!.SendAsync(req, Ct);
        var raw = await resp.Content.ReadAsStringAsync(Ct);
        return (resp.StatusCode, raw.Length > 0 && raw.TrimStart().StartsWith('{') ? JsonNode.Parse(raw) : null, raw);
    }

    private static object WhoA(Guid? secure = null) =>
        new { agentId = A, home = "", sessionId = SessionA, secureSessionId = secure ?? SecureA };

    [Fact]
    public async Task No_key_is_401_with_empty_body()
    {
        await StartAsync();

        var (status, _, raw) = await Post("balance", WhoA(), key: null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("", raw);
    }

    [Fact]
    public async Task Wrong_key_is_401()
    {
        await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("balance", WhoA(), key: Key + "x")).Status);
    }

    [Fact]
    public async Task Twenty_bad_keys_then_429()
    {
        await StartAsync();
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post("health", null, key: "nope")).Status);

        Assert.Equal((HttpStatusCode)429, (await Post("health", null)).Status);
    }

    [Fact]
    public async Task Health_answers_ok()
    {
        await StartAsync();

        var (status, body, _) = await Post("health", null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Balance_for_avatar_in_my_region_is_ok()
    {
        await StartAsync();

        var (status, body, _) = await Post("balance", WhoA());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", (string?)body!["code"]);
        Assert.Equal(100, body["balance"]!.GetValue<long>());
    }

    [Fact]
    public async Task Balance_for_avatar_elsewhere_is_wrong_region()
    {
        var db = await StartAsync();
        await db.Sql($"UPDATE `robust`.`Presence` SET RegionID = '{There}'");

        Assert.Equal("wrong_region", (string?)(await Post("balance", WhoA())).Body!["code"]);
    }

    [Fact]
    public async Task Stale_session_is_no_session()
    {
        var db = await StartAsync();
        await db.Sql("DELETE FROM `robust`.`Presence`");

        Assert.Equal("no_session", (string?)(await Post("balance", WhoA())).Body!["code"]);
    }

    [Fact]
    public async Task Forged_secure_session_is_no_session()
    {
        await StartAsync();

        Assert.Equal("no_session", (string?)(await Post("balance", WhoA(Guid.NewGuid()))).Body!["code"]);
    }

    [Fact]
    public async Task Transfer_moves_coins()
    {
        await StartAsync();

        var (_, body, _) = await Post("transfer", new
        {
            txnId = Guid.NewGuid(), type = "pay_avatar", from = WhoA(), to = new { agentId = B, home = "" }, amount = 10, regionId = Here,
        });

        Assert.Equal("ok", (string?)body!["code"]);
        Assert.Equal(90, body["balance"]!.GetValue<long>());
        Assert.Equal(110, body["toBalance"]!.GetValue<long>());
    }

    [Fact]
    public async Task Transfer_for_region_outside_grant_is_wrong_region()
    {
        await StartAsync();

        var (_, body, _) = await Post("transfer", new
        {
            txnId = Guid.NewGuid(), type = "pay_avatar", from = WhoA(), to = new { agentId = B, home = "" }, amount = 10, regionId = There,
        });

        Assert.Equal("wrong_region", (string?)body!["code"]);
    }

    [Fact]
    public async Task Transfer_type_other_than_payments_is_bad_request()
    {
        await StartAsync();

        var (status, body, _) = await Post("transfer", new
        {
            txnId = Guid.NewGuid(), type = "staff_gift", from = WhoA(), to = new { agentId = B, home = "" }, amount = 10, regionId = Here,
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", (string?)body!["code"]);
    }

    [Fact]
    public async Task Repeated_txn_returns_same_answer()
    {
        await StartAsync();
        var transfer = new { txnId = Guid.NewGuid(), type = "pay_avatar", from = WhoA(), to = new { agentId = B, home = "" }, amount = 10, regionId = Here };

        var first = await Post("transfer", transfer);
        var second = await Post("transfer", transfer);

        Assert.Equal(first.Raw, second.Raw);
        Assert.Equal(90, (await Post("balance", WhoA())).Body!["balance"]!.GetValue<long>());
    }

    [Fact]
    public async Task Hold_then_complete_over_http()
    {
        await StartAsync();

        var (_, hold, _) = await Post("hold", new
        {
            txnId = Guid.NewGuid(), kind = "object", buyer = WhoA(), seller = new { agentId = B, home = "" }, amount = 25, regionId = Here,
            objectId = Guid.NewGuid(), objectName = "dress",
        });
        Assert.Equal("ok", (string?)hold!["code"]);
        var holdId = hold["holdId"]!.GetValue<long>();

        var (_, done, _) = await Post("hold/complete", new { txnId = Guid.NewGuid(), holdId });

        Assert.Equal("ok", (string?)done!["code"]);
        Assert.Equal(125, done["toBalance"]!.GetValue<long>());
    }

    [Fact]
    public async Task Unknown_hold_is_unknown_hold()
    {
        await StartAsync();

        Assert.Equal("unknown_hold", (string?)(await Post("hold/return", new { txnId = Guid.NewGuid(), holdId = 4242 })).Body!["code"]);
    }

    [Fact]
    public async Task Payout_needs_opt_in()
    {
        await StartAsync();

        var (_, body, _) = await Post("payout", new
        {
            txnId = Guid.NewGuid(), ownerId = A, to = new { agentId = B, home = "" }, amount = 5, regionId = Here, objectId = Guid.NewGuid(), objectName = "slot",
        });

        Assert.Equal("not_allowed", (string?)body!["code"]);
    }

    [Fact]
    public async Task Charge_takes_a_fee()
    {
        await StartAsync();

        var (_, body, _) = await Post("charge", new { txnId = Guid.NewGuid(), type = "upload_fee", payer = WhoA(), amount = 3 });

        Assert.Equal("ok", (string?)body!["code"]);
        Assert.Equal(97, body["balance"]!.GetValue<long>());
    }

    [Fact]
    public async Task Economy_answers_settings()
    {
        await StartAsync();

        var (_, body, _) = await Post("economy", null);

        Assert.Equal("C$", (string?)body!["currencySymbol"]);
        Assert.Equal(3, body["uploadFee"]!.GetValue<long>());
        Assert.Equal(0, body["classifiedFee"]!.GetValue<long>());
    }

    [Fact]
    public async Task Database_down_returns_unavailable_code()
    {
        await StartAsync(dbOverride: s => s with { MariaDbPort = MoneyMariaDb.FreePort() });

        var (status, body, raw) = await Post("balance", WhoA());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("unavailable", (string?)body!["code"]);
        Assert.DoesNotContain("Exception", raw);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_json_is_400_bad_request()
    {
        await StartAsync();

        var (status, body, _) = await Post("balance", "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", (string?)body!["code"]);
    }

    [Fact]
    public async Task Unknown_field_is_400()
    {
        await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await Post("balance", new { agentId = A, home = "", sessionId = SessionA, secureSessionId = SecureA, extra = 1 })).Status);
    }

    [Fact]
    public async Task Missing_field_is_400()
    {
        await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await Post("balance", new { agentId = A, home = "" })).Status);
    }

    [Fact]
    public async Task Fractional_amount_is_400()
    {
        await StartAsync();

        var body = $$"""{"txnId":"{{Guid.NewGuid()}}","type":"pay_avatar","from":{"agentId":"{{A}}","home":"","sessionId":"{{SessionA}}","secureSessionId":"{{SecureA}}"},"to":{"agentId":"{{B}}","home":""},"amount":1.5,"regionId":"{{Here}}"}""";

        Assert.Equal(HttpStatusCode.BadRequest, (await Post("transfer", body)).Status);
    }

    [Fact]
    public async Task Listens_on_loopback_only()
    {
        await StartAsync();
        var lan = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip));
        if (lan is null) Assert.Skip("This computer has no LAN address.");

        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await tcp.ConnectAsync(lan, _port, Ct));
    }

    [Fact]
    public async Task Off_mode_does_not_listen()
    {
        await StartAsync(MoneyMode.Off);

        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await tcp.ConnectAsync(IPAddress.Loopback, _port, Ct));
    }

    [Fact]
    public async Task Logs_never_contain_the_key()
    {
        await StartAsync();
        for (var i = 0; i < 25; i++) await Post("health", null, key: "bad-" + Key);
        await Post("balance", WhoA());
        await Post("transfer", new { txnId = Guid.NewGuid(), type = "pay_avatar", from = WhoA(), to = new { agentId = B, home = "" }, amount = 1, regionId = Here });

        Assert.NotEmpty(_logs);
        Assert.All(_logs, line =>
        {
            Assert.DoesNotContain(Key, line);
            Assert.DoesNotContain(SecureA.ToString(), line);
            Assert.DoesNotContain(SessionA.ToString(), line);
        });
    }

    [Fact]
    public async Task Random_bodies_never_500()
    {
        await StartAsync();
        var rng = new Random(1234);
        string[] paths = ["balance", "transfer", "payout", "charge", "hold", "hold/complete", "hold/return", "economy", "health"];
        string[] pieces = ["{", "}", "[", "]", ",", ":", "\"agentId\"", "\"amount\"", "\"txnId\"", "\"from\"", "\"home\"", "null", "true", "-1", "1e9",
            "99999999999999999999", "\"x\"", $"\"{A}\"", "\"\"", "0", "\"holdId\"", "\"type\"", "\"pay_avatar\"", "{\"agentId\":1}"];

        foreach (var path in paths)
            for (var i = 0; i < 500; i++)
            {
                var body = string.Concat(Enumerable.Range(0, rng.Next(1, 14)).Select(_ => pieces[rng.Next(pieces.Length)]));
                var status = (await Post(path, body)).Status;
                Assert.True(status is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized,
                    $"{path} answered {(int)status} to {body}");
            }
    }

    private sealed class QueueLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(sink, categoryName);

        public void Dispose() { }

        private sealed class QueueLogger(ConcurrentQueue<string> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
