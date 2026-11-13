using System.Net;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Dashboard;

/// <summary>Sent by name ("CheckThis") in /api/dashboard.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ServiceState>))]
public enum ServiceState { Working, Slow, CheckThis, Down }

/// <param name="Detail">The small line under the name: the port, or what was found.</param>
/// <param name="Milliseconds">How long the answer took; null when there was none.</param>
public sealed record ServiceCheck(string Name, string Detail, ServiceState State, long? Milliseconds);

/// <summary>Checks the grid's services answer; the host can swap in a test double.</summary>
public interface IServiceChecks
{
    /// <param name="profilesRead">Whether the profile counts could be read: Profiles has no cheap HTTP check (JSON-RPC only).</param>
    Task<IReadOnlyList<ServiceCheck>> CheckAsync(bool profilesRead, CancellationToken ct);
}

/// <summary>
/// The Core services rows (design 2026-11-05-dashboard). [S] Every request goes to 127.0.0.1 and a port from settings only, with
/// the "local-checks" client: a 2-second timeout, no redirects, at most 64 KB read, no credentials. Only where this computer runs
/// Robust; elsewhere there are no rows.
/// </summary>
public sealed class ServiceChecks(IHttpClientFactory clients, Func<LauncherSettings> settings, TimeProvider time) : IServiceChecks
{
    public const string ClientName = "local-checks";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(1);
    public const int MaxBytes = 64 * 1024;

    /// <summary>OpenSim's default "plywood" texture, in every asset set.</summary>
    public const string DefaultTexture = "89556747-24cb-43ed-920b-47caed15465f";

    private const string Zero = "00000000-0000-0000-0000-000000000000";

    public async Task<IReadOnlyList<ServiceCheck>> CheckAsync(bool profilesRead, CancellationToken ct)
    {
        var s = settings();
        if (!PlanCatalog.RunsRobust(s)) return [];
        var pub = $"http://127.0.0.1:{s.RobustPublicPort}";
        var priv = $"http://127.0.0.1:{s.RobustPrivatePort}";
        var port = $"port {s.RobustPublicPort}";
        var privatePort = $"port {s.RobustPrivatePort}";
        var pending = new List<Task<ServiceCheck>>
        {
            CheckOneAsync("Login", port, Get(pub + "/get_grid_info"),
                (code, body) => code == HttpStatusCode.OK && body.Contains("<gridinfo", StringComparison.OrdinalIgnoreCase) ? ServiceState.Working : ServiceState.CheckThis, ct),
        };
        if (PlanCatalog.HypergridOn(s))
            // HEAD: Robust logs every GET of /helo as obsolete.
            pending.Add(CheckOneAsync("Hypergrid", port, () => new HttpRequestMessage(HttpMethod.Head, pub + "/helo"),
                (code, _) => code == HttpStatusCode.OK ? ServiceState.Working : ServiceState.CheckThis, ct));
        pending.Add(CheckOneAsync("Textures", port, () =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, pub + "/CAPS/GetTexture/?texture_id=" + DefaultTexture);
                r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
                return r;
            },
            // 404: the texture service isn't loaded (the PR 35 bug) or the default texture is missing; both need a look.
            (code, _) => code is HttpStatusCode.OK or HttpStatusCode.PartialContent ? ServiceState.Working : ServiceState.CheckThis, ct));
        // The default texture's metadata: a 404 would be the same whether the asset or the whole service is missing.
        pending.Add(CheckOneAsync("Assets", privatePort, Get(priv + "/assets/" + DefaultTexture + "/metadata"),
            (code, body) => code == HttpStatusCode.OK && body.Contains("<AssetMetadata", StringComparison.Ordinal) ? ServiceState.Working : ServiceState.CheckThis, ct));
        // A harmless read: the account with the zero id, which doesn't exist. Robust answers 200 even when it fails, so the
        // answer must be the "no such account" one.
        pending.Add(CheckOneAsync("Accounts", privatePort, () => new HttpRequestMessage(HttpMethod.Post, priv + "/accounts")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["METHOD"] = "getaccount", ["ScopeID"] = Zero, ["UserID"] = Zero, ["VERSIONMIN"] = "0", ["VERSIONMAX"] = "0",
                }),
            },
            (code, body) => code == HttpStatusCode.OK && body.Contains("<result>null</result>", StringComparison.OrdinalIgnoreCase) ? ServiceState.Working : ServiceState.CheckThis, ct));
        // In parallel: each has its own 2-second limit, so the slowest decides, not the sum.
        var checks = (await Task.WhenAll(pending)).ToList();
        checks.Add(new ServiceCheck("Profiles", profilesRead ? "read from the database" : "the profile tables couldn't be read",
            profilesRead ? ServiceState.Working : ServiceState.CheckThis, null));
        return checks;
    }

    private static Func<HttpRequestMessage> Get(string url) => () => new HttpRequestMessage(HttpMethod.Get, url);

    private async Task<ServiceCheck> CheckOneAsync(string name, string detail, Func<HttpRequestMessage> request,
        Func<HttpStatusCode, string, ServiceState> judge, CancellationToken ct)
    {
        var client = clients.CreateClient(ClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var started = time.GetTimestamp();
        try
        {
            using var message = request();
            if (message.RequestUri is not { Host: "127.0.0.1" }) throw new InvalidOperationException("Checks go to 127.0.0.1 only.");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await ReadSomeAsync(response, timeout.Token);
            var took = time.GetElapsedTime(started);
            var state = judge(response.StatusCode, body);
            if (state == ServiceState.Working && took > SlowAfter) state = ServiceState.Slow;
            return new ServiceCheck(name, detail, state, (long)took.TotalMilliseconds);
        }
        // A refused or dropped connection, a timeout, a body cut off mid-read: the service didn't answer.
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new ServiceCheck(name, detail, ServiceState.Down, null);
        }
    }

    /// <summary>At most <see cref="MaxBytes"/> of the body, as text; the rest is never read.</summary>
    private static async Task<string> ReadSomeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
