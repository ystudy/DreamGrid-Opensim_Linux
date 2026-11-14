using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Money;

/// <summary>
/// The money service's endpoints (money plan 1, task 9): POST, JSON, under <c>/money/v1/</c>, authenticated by
/// <c>Authorization: GridGateRegion &lt;key&gt;</c>. Answers are <c>{ "code": … }</c> with HTTP 200; a bad key is a bare
/// 401, a blocked address a bare 429, and a body that doesn't fit is 400 <c>{ "code": "bad_request" }</c>.
/// </summary>
internal static class MoneyApi
{
    private const string Scheme = "GridGateRegion ";
    private static readonly string[] PaymentTypes = [EntryType.PayAvatar, EntryType.PayObject, EntryType.ParcelPass];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Request bodies. Every field is nullable so a missing one is a 400 from Need, not a default value.
    internal sealed record Who(Guid? AgentId, string? Home, Guid? SessionId, Guid? SecureSessionId);
    internal sealed record TransferBody(Guid? TxnId, string? Type, Who? From, Who? To, long? Amount, Guid? RegionId, Guid? ObjectId, string? ObjectName);
    internal sealed record PayoutBody(Guid? TxnId, Guid? OwnerId, Who? To, long? Amount, Guid? RegionId, Guid? ObjectId, string? ObjectName);
    internal sealed record ChargeBody(Guid? TxnId, string? Type, Who? Payer, long? Amount, Guid? PayTo);
    internal sealed record HoldBody(Guid? TxnId, string? Kind, Who? Buyer, Who? Seller, long? Amount, Guid? RegionId, Guid? ObjectId, string? ObjectName);
    internal sealed record SettleBody(Guid? TxnId, long? HoldId);

    // Answers.
    internal sealed record Answer(string Code, long? Balance = null, long? ToBalance = null, long? EntryId = null, long? HoldId = null);
    internal sealed record EconomyAnswer(string Code, string CurrencySymbol, long UploadFee, long ClassifiedFee);
    internal sealed record HealthAnswer(bool Ok);

    private sealed class BadRequest : Exception;

    internal sealed record Context(MoneyLedger Ledger, PresenceCheck Presence, Func<LauncherSettings> Settings, MoneyAuthFailures Failures, ILogger Log);

    public static void Map(IEndpointRouteBuilder app, Context c)
    {
        Post(app, "/money/v1/health", c, (_, _, _) => Task.FromResult<object>(new HealthAnswer(true)));
        Post(app, "/money/v1/economy", c, (_, _, _) =>
        {
            var m = c.Settings().Money;
            return Task.FromResult<object>(new EconomyAnswer("ok", m.CurrencySymbol, m.UploadFee, m.ClassifiedFee));
        });
        Post(app, "/money/v1/balance", c, async (http, grant, ct) =>
        {
            var w = await Read<Who>(http, ct);
            return await WithSession(c, w, grant, ct) ?? Of(await c.Ledger.BalanceAsync(Account(w), ct));
        });
        Post(app, "/money/v1/transfer", c, async (http, grant, ct) =>
        {
            var b = await Read<TransferBody>(http, ct);
            var type = Need(b.Type);
            if (!PaymentTypes.Contains(type)) throw new BadRequest();
            var from = Need(b.From);
            var region = Need(b.RegionId);
            if (!grant.Regions.Contains(region)) return Code("wrong_region");
            return await WithSession(c, from, grant, ct) ?? Of(await c.Ledger.TransferAsync(
                new TransferRequest(Need(b.TxnId), type, Account(from), Account(Need(b.To)), Need(b.Amount), region, b.ObjectId, b.ObjectName), ct));
        });
        Post(app, "/money/v1/payout", c, async (http, grant, ct) =>
        {
            var b = await Read<PayoutBody>(http, ct);
            var region = Need(b.RegionId);
            var request = new PayoutRequest(Need(b.TxnId), Need(b.OwnerId), Account(Need(b.To)), Need(b.Amount), region, Need(b.ObjectId), Need(b.ObjectName));
            if (!grant.Regions.Contains(region)) return Code("wrong_region");
            return Of(await c.Ledger.PayoutAsync(request, ct));
        });
        Post(app, "/money/v1/charge", c, async (http, grant, ct) =>
        {
            var b = await Read<ChargeBody>(http, ct);
            var payer = Need(b.Payer);
            var request = new ChargeRequest(Need(b.TxnId), Need(b.Type), Account(payer), Need(b.Amount), b.PayTo);
            return await WithSession(c, payer, grant, ct) ?? Of(await c.Ledger.ChargeAsync(request, ct));
        });
        Post(app, "/money/v1/hold", c, async (http, grant, ct) =>
        {
            var b = await Read<HoldBody>(http, ct);
            var buyer = Need(b.Buyer);
            var region = Need(b.RegionId);
            var request = new HoldRequest(Need(b.TxnId), Need(b.Kind), Account(buyer), Account(Need(b.Seller)), Need(b.Amount), region, b.ObjectId, b.ObjectName);
            if (!grant.Regions.Contains(region)) return Code("wrong_region");
            return await WithSession(c, buyer, grant, ct) ?? Of(await c.Ledger.HoldAsync(request, ct));
        });
        Post(app, "/money/v1/hold/complete", c, (http, grant, ct) => Settle(http, c, grant, complete: true, ct));
        Post(app, "/money/v1/hold/return", c, (http, grant, ct) => Settle(http, c, grant, complete: false, ct));
    }

    /// <summary>One endpoint's work, given its authenticated key's grant; returns the answer to write.</summary>
    private delegate Task<object> Endpoint(HttpContext http, RegionKeyGrant grant, CancellationToken ct);

    private static void Post(IEndpointRouteBuilder app, string path, Context c, Endpoint endpoint) =>
        app.MapPost(path, (RequestDelegate)(http => Handle(http, c, endpoint)));

    private static async Task<object> Settle(HttpContext http, Context c, RegionKeyGrant grant, bool complete, CancellationToken ct)
    {
        var b = await Read<SettleBody>(http, ct);
        var (txn, hold) = (Need(b.TxnId), Need(b.HoldId));
        // Only the region that made a hold may settle it.
        if (await c.Ledger.HoldRegionAsync(hold, ct) is not { } region) return Code("unknown_hold");
        if (!grant.Regions.Contains(region)) return Code("wrong_region");
        return Of(complete ? await c.Ledger.CompleteHoldAsync(txn, hold, ct) : await c.Ledger.ReturnHoldAsync(txn, hold, ct));
    }

    /// <summary>
    /// Authenticates, runs the endpoint and writes its answer. Nothing here logs what was sent: no key, no session id, no body.
    /// </summary>
    private static async Task Handle(HttpContext http, Context c, Endpoint endpoint)
    {
        var ct = http.RequestAborted;
        var ip = http.Connection.RemoteIpAddress ?? IPAddress.None;
        if (c.Failures.IsBlocked(ip))
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        var header = http.Request.Headers.Authorization.ToString();
        var key = header.StartsWith(Scheme, StringComparison.Ordinal) ? header[Scheme.Length..] : null;
        if (!RegionKeyRing.FromSettings(c.Settings()).TryAuthenticate(key, out var grant) || grant is null)
        {
            c.Failures.RecordFailure(ip);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        object answer;
        try
        {
            answer = await endpoint(http, grant, ct);
        }
        catch (Exception e) when (e is BadRequest or JsonException or NotSupportedException)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            answer = Code("bad_request");
        }
        catch (MoneyUnavailableException)
        {
            answer = Code("unavailable");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.Log.LogError("A money request failed with {Error}.", e.GetType().Name);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }
        http.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(http.Response.Body, answer, answer.GetType(), Json, ct);
    }

    private static async Task<T> Read<T>(HttpContext http, CancellationToken ct) where T : class =>
        await JsonSerializer.DeserializeAsync<T>(http.Request.Body, Json, ct) ?? throw new BadRequest();

    /// <summary>Null when <paramref name="w"/>'s session is live in one of the key's regions; otherwise the refusal.</summary>
    private static async Task<Answer?> WithSession(Context c, Who w, RegionKeyGrant grant, CancellationToken ct) =>
        await c.Presence.CheckAsync(Need(w.AgentId), Need(w.SessionId), Need(w.SecureSessionId), grant.Regions, ct) switch
        {
            PresenceOutcome.Ok => null,
            PresenceOutcome.WrongRegion => Code("wrong_region"),
            PresenceOutcome.Unavailable => Code("unavailable"),
            _ => Code("no_session"),
        };

    private static AccountRef Account(Who w) => new(Need(w.AgentId), w.Home ?? "");

    private static Answer Of(MoneyResult r) =>
        new(JsonNamingPolicy.SnakeCaseLower.ConvertName(r.Outcome.ToString()), r.FromBalance, r.ToBalance, r.EntryId, r.HoldId);

    private static Answer Code(string code) => new(code);

    private static T Need<T>(T? value) where T : class => value ?? throw new BadRequest();

    private static T Need<T>(T? value) where T : struct => value ?? throw new BadRequest();
}
