using System.Net;
using DreamGrid.Launcher.Core.Api;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher;

/// <summary>The port-8001 contract (spec 5) that the patched OpenSim and Robust call, on the web port.</summary>
public static class LegacyEndpoints
{
    private const string TextPlain = "text/plain; charset=utf-8";
    private const int MaxAlertBytes = 64 * 1024;

    public static void Map(WebApplication app)
    {
        app.MapMethods("/", [HttpMethods.Get, HttpMethods.Post], Root);
        app.MapPost("/dreamgrid/regionready", RegionReady);
        app.MapGet("/welcome", Welcome);
    }

    private static async Task<IResult> Root(HttpContext ctx, LegacyCommandApi api, ILoggerFactory logs)
    {
        var log = logs.CreateLogger(typeof(LegacyEndpoints).FullName!);
        var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ctx.RequestAborted) : null;
        var request = LegacyRequestParser.From(ctx.Request.Query, form);

        // SmartStart is off in the generated INIs; if it is called anyway, never redirect (spec 5).
        if (request.Raw.TryGetValue("alt", out var alt))
            return Results.Text(alt, TextPlain);

        if (request.Raw.TryGetValue("type", out var type))
        {
            request.Raw.TryGetValue("UUID", out var uuid);
            log.LogInformation("SmartStart notify {Type} for {Uuid}", type, uuid);
            return Results.Text("ACK", TextPlain);
        }

        if (request.Raw.ContainsKey("command"))
        {
            var reply = await api.HandleAsync(request, ctx.RequestAborted);
            // Never the password, avatar or MachineId: the command and how it ended only.
            log.LogInformation("Command {Command}: {Result}", request.Command, Outcome(reply));
            return Results.Text(reply, TextPlain);
        }

        return Results.Text("Test Completed", TextPlain); // the old DreamGrid port probe
    }

    private static string Outcome(string reply) =>
        reply.StartsWith("NAK You do not have permission", StringComparison.Ordinal) ? "NAK permission"
        : reply.StartsWith("NAK", StringComparison.Ordinal) ? "NAK"
        : reply == "ACK" ? "ACK" : "data";

    /// <summary>The RegionReady alert_uri. Only the region on this machine posts here.</summary>
    private static async Task<IResult> RegionReady(HttpContext ctx, IGridController grid)
    {
        if (ctx.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        using var reader = new StreamReader(ctx.Request.Body);
        var buffer = new char[MaxAlertBytes];
        var read = await reader.ReadBlockAsync(buffer, ctx.RequestAborted);
        if (read == MaxAlertBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        if (RegionAlert.Parse(new string(buffer, 0, read)) is not { } alert) return Results.BadRequest();
        grid.OnRegionAlert(alert);
        return Results.Ok();
    }

    /// <summary>The GridInfoService welcome page; plan 2b replaces it.</summary>
    private static IResult Welcome(Func<LauncherSettings> settings)
    {
        var name = WebUtility.HtmlEncode(settings().GridName);
        return Results.Content(
            $"<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>{name}</title></head>"
            + $"<body><h1>Welcome to {name}</h1></body></html>\n",
            "text/html; charset=utf-8");
    }
}
