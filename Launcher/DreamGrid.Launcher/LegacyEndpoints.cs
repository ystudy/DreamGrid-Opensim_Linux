using System.Net;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Api;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;

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
}
