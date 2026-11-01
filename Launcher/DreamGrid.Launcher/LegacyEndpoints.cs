using System.Net;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Api;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;

namespace DreamGrid.Launcher;

/// <summary>Marks the port-8001 handler on "/".</summary>
internal sealed class LegacyRootMetadata;

/// <summary>
/// Chooses between the legacy handler and the dashboard page, which both match "/". A GET or HEAD without alt, type
/// or command is the website's; a POST, or a GET or HEAD with one of those keys, is the legacy contract's (the
/// dashboard's own forms post to /dashboard).
/// </summary>
internal sealed class LegacyRootPolicy : Microsoft.AspNetCore.Routing.MatcherPolicy, Microsoft.AspNetCore.Routing.Matching.IEndpointSelectorPolicy
{
    private static readonly string[] Keys = ["alt", "type", "command"];

    public override int Order => -1000;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => endpoints.Any(e => e.Metadata.GetMetadata<LegacyRootMetadata>() is not null);

    public Task ApplyAsync(HttpContext ctx, Microsoft.AspNetCore.Routing.Matching.CandidateSet candidates)
    {
        var method = ctx.Request.Method;
        var legacy = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) || Keys.Any(ctx.Request.Query.ContainsKey);
        for (var i = 0; i < candidates.Count; i++)
            if ((candidates[i].Endpoint.Metadata.GetMetadata<LegacyRootMetadata>() is not null) != legacy)
                candidates.SetValidity(i, false);
        return Task.CompletedTask;
    }
}

/// <summary>The port-8001 contract (spec 5) that the patched OpenSim and Robust call, on the web port.</summary>
public static class LegacyEndpoints
{
    private const string TextPlain = "text/plain; charset=utf-8";
    private const int MaxAlertBytes = 64 * 1024;
    private const int MaxLoggedChars = 64;

    public static void Map(WebApplication app)
    {
        // Open on purpose: the patched OpenSim and Robust call these without signing in (SetupGate and the loopback check guard them).
        app.MapMethods("/", [HttpMethods.Get, HttpMethods.Post], Root).WithMetadata(new LegacyRootMetadata()).AllowAnonymous();
        app.MapPost("/dreamgrid/regionready", RegionReady).AllowAnonymous();
        app.MapGet("/dreamgrid/terms", TermsCheck).AllowAnonymous();
        app.MapPost("/dreamgrid/terms", TermsAgreed).AllowAnonymous();
    }

    private static async Task<IResult> Root(HttpContext ctx, LegacyCommandApi api, IHostApplicationLifetime lifetime, ILoggerFactory logs)
    {
        var log = logs.CreateLogger(typeof(LegacyEndpoints).FullName!);
        var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ctx.RequestAborted) : null;
        var request = LegacyRequestParser.From(ctx.Request.Query, form);

        // SmartStart is off in the generated INIs; if it is called anyway, never redirect (spec 5).
        if (request.Raw.TryGetValue("alt", out var alt))
            return Text(ctx, alt);

        if (request.Raw.TryGetValue("type", out var type))
        {
            request.Raw.TryGetValue("UUID", out var uuid);
            log.LogInformation("SmartStart notify {Type} for {Uuid}", Clean(type), Clean(uuid));
            return Text(ctx, "ACK");
        }

        if (request.Raw.ContainsKey("command"))
        {
            // Not RequestAborted: a caller that goes away must not leave a component half-stopped.
            var reply = await api.HandleAsync(request, ctx.Connection.RemoteIpAddress, lifetime.ApplicationStopping);
            // Never the password, avatar or MachineId: the command and how it ended only.
            log.LogInformation("Command {Command}: {Result}", Clean(request.Command), Outcome(reply));
            return Text(ctx, reply);
        }

        // Unreachable for a bare GET (LegacyRootPolicy leaves that to the website); a POST with no known key.
        return Results.NotFound();
    }

    /// <summary>True for 127.0.0.0/8 and ::1, including IPv4-mapped forms such as ::ffff:127.0.0.1.</summary>
    public static bool IsLoopbackCaller(IPAddress remote) =>
        IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);

    /// <summary>An untrusted value made safe for a log line: control characters become '?', at most 64 characters.</summary>
    private static string Clean(string? value) => LogText.Clean(value, MaxLoggedChars);

    private static IResult Text(HttpContext ctx, string body)
    {
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Text(body, TextPlain);
    }

    private static string Outcome(string reply) =>
        reply.StartsWith("NAK You do not have permission", StringComparison.Ordinal) ? "NAK permission"
        : reply.StartsWith("NAK", StringComparison.Ordinal) ? "NAK"
        : reply == "ACK" ? "ACK" : "data";

    /// <summary>The RegionReady alert_uri. Only the region on this machine posts here.</summary>
    private static async Task<IResult> RegionReady(HttpContext ctx, IGridController grid)
    {
        if (ctx.Connection.RemoteIpAddress is { } remote && !IsLoopbackCaller(remote))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        using var reader = new StreamReader(ctx.Request.Body);
        var buffer = new char[MaxAlertBytes];
        var read = await reader.ReadBlockAsync(buffer, ctx.RequestAborted);
        if (read == MaxAlertBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        if (RegionAlert.Parse(new string(buffer, 0, read)) is not { } alert) return Results.BadRequest();
        grid.OnRegionAlert(alert);
        return Results.Ok();
    }

    /// <summary>
    /// Robust's login asks whether the avatar (<c>id</c>, at <c>level</c>) must accept the operator's Terms first: "ASK" or "OK".
    /// Only Robust on this machine asks. A website acceptance counts here too (<see cref="TermsAcceptances"/>).
    /// </summary>
    private static IResult TermsCheck(HttpContext ctx, Func<LauncherSettings> settings, TermsAcceptances acceptances)
    {
        if (ctx.Connection.RemoteIpAddress is { } remote && !IsLoopbackCaller(remote))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!TryAvatar(ctx.Request.Query["id"], ctx.Request.Query["level"], out var id, out var level)) return Results.BadRequest();
        return Text(ctx, TermsToAccept(settings(), acceptances, id, level) is null ? "OK" : "ASK");
    }

    /// <summary>
    /// Robust says the avatar ticked "I agree" in the viewer's Terms window: the current version is recorded with the viewer's
    /// address (<c>ip</c>), as an acceptance on the website would be. Only Robust on this machine says so.
    /// </summary>
    private static async Task<IResult> TermsAgreed(HttpContext ctx, Func<LauncherSettings> settings, TermsAcceptances acceptances, AuditLog audit)
    {
        if (ctx.Connection.RemoteIpAddress is { } remote && !IsLoopbackCaller(remote))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!ctx.Request.HasFormContentType) return Results.BadRequest();
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!TryAvatar(form["id"], form["level"], out var id, out var level)) return Results.BadRequest();

        if (TermsToAccept(settings(), acceptances, id, level) is { } terms)
        {
            // An address that doesn't parse is not kept: the value is Robust's, but the record is shown to staff.
            var ip = IPAddress.TryParse(form["ip"], out var address) ? address.ToString() : null;
            acceptances.Accept(id, terms.Version, ip);
            audit.Write(new AuditEntry(Clean(form["name"]), id, AuthSchemes.GuestRole, ip,
                "terms-accepted", "terms v" + terms.Version + " in the viewer", "ok"));
        }
        return Text(ctx, "OK");
    }

    /// <summary>
    /// The Terms the avatar must accept before logging in: the current text, unless there is none, the avatar is the owner or
    /// staff (who are never asked, so the grid can't lock out its own people), or they have accepted this version already.
    /// </summary>
    private static LegalText? TermsToAccept(LauncherSettings s, TermsAcceptances acceptances, Guid id, int level)
    {
        if (LegalPages.Get(s, LegalPages.Terms) is not { } terms) return null;
        if (AdminRule.RoleOf(s, new RobustAccount(id, "", "", level, "")) != StaffRole.None) return null;
        return acceptances.HasAccepted(id, terms.Version) ? null : terms;
    }

    private static bool TryAvatar(string? id, string? level, out Guid principal, out int userLevel)
    {
        userLevel = 0;
        return Guid.TryParse(id, out principal) && principal != Guid.Empty && int.TryParse(level, out userLevel);
    }
}
