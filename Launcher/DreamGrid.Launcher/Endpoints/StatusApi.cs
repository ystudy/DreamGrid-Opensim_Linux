using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Antiforgery;

namespace DreamGrid.Launcher.Endpoints;

/// <param name="Name">"database", "robust" or "region".</param>
/// <param name="Label">What the dashboard calls it: "Database", "Grid service", "Your region".</param>
/// <param name="Detail">Uptime and memory in words, or "".</param>
public sealed record ComponentView(string Name, string Label, string State, string StateText, string Detail);

public sealed record GridView(ComponentView Database, ComponentView Robust, ComponentView? Region, bool LoginsEnabled, string LoginUri);

/// <summary>What the dashboard shows and <c>GET /api/status</c> returns: names and states only, no paths or secrets.</summary>
public sealed record StatusView(GridView Grid, IReadOnlyList<string> Online, bool OnlineAvailable, string? Problem, string Headline);

public static class StatusApi
{
    public static void Map(WebApplication app)
    {
        // Open to anyone (the port-8001 contract already tells the LAN the region status); avatar names only for an admin.
        app.MapGet("/api/status", async (HttpContext ctx, IGridController grid, IOnlineUsers online) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(await BuildAsync(grid, online, ctx.User.IsInRole(AuthSchemes.AdminRole), ctx.RequestAborted));
        });

        app.MapPost("/api/grid/{action}", async (string action, HttpContext ctx, IAntiforgery antiforgery, GridActions actions) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx))
                return Results.BadRequest("The request verification token is missing or wrong. Reload the page and try again.");
            return actions.TryRun(action)
                ? Results.Accepted(value: new { notice = GridActions.Notices[action] })
                : Results.NotFound();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
    }

    /// <summary>The status as the dashboard shows it; who's online is asked only for an admin and a running database.</summary>
    public static async Task<StatusView> BuildAsync(IGridController grid, IOnlineUsers online, bool withOnline, CancellationToken ct)
    {
        var s = grid.GetStatus();
        var people = withOnline && s.Database.State == ProcessState.Running ? await online.ListAsync(ct) : OnlineList.Unavailable;
        var problem = Problem(s);
        return new StatusView(
            new GridView(View(s.Database, "Database"), View(s.Robust, "Grid service"),
                s.Region is { } r ? View(r, "Your region") : null, s.LoginsEnabled, s.LoginUri),
            people.Avatars.Select(a => a.Name).ToList(), people.Available, problem, problem ?? Headline(s));
    }

    public const string CouldNotStart = "Your world could not start.";

    /// <summary>
    /// In plain words: the last failed start (unless everything runs again), else the first component (in start
    /// order) that failed or crashed, else (with nothing under way) one that is stopped while another runs; null when
    /// the world is running, starting, stopping, restarting or wholly stopped.
    /// </summary>
    public static string? Problem(GridStatus s)
    {
        if (ShowsStartFailure(s)) return PlainStartFailure(s.StartFailure!);
        var parts = Parts(s).ToList();
        if (parts.FirstOrDefault(p => p.C.State is ProcessState.Failed or ProcessState.Crashed) is { C: not null } down)
            return down.Message;
        // Between components of a start or stop every state looks settled; only a settled partial grid is a problem.
        if (InTransition(s)) return null;
        if (parts.Any(p => p.C.State == ProcessState.Running) &&
            parts.FirstOrDefault(p => p.C.State == ProcessState.Stopped) is { C: not null } stopped)
            return stopped.Message;
        return null;
    }

    /// <summary>
    /// True while the last start failure is worth showing: not while a retry or other operation is under way, and a
    /// stale one is ignored once everything runs.
    /// </summary>
    public static bool ShowsStartFailure(GridStatus s) => s.StartFailure is not null && s.Activity == GridActivity.None && !AllRunning(s);

    /// <summary>
    /// Built from the port number, never from the raw message (which uses 2a's internal names and may hold paths).
    /// </summary>
    private static string PlainStartFailure(StartFailure f) => f.ConflictPort is { } port
        ? PortInUse(port) + " Close that program, or pick another port in Advanced Settings \u2192 Network."
        : CouldNotStart;

    /// <summary>The plain port-conflict sentence; callers add what to do (setup pages cannot reach Advanced Settings).</summary>
    public static string PortInUse(int port) => $"Port {port} is already in use by another program, so your world can't start.";

    /// <summary>
    /// The port a failure was about: the exception's own, else that of the last failed start while it is still shown.
    /// </summary>
    public static int? ConflictPort(Exception e, GridStatus s) =>
        (e as PortConflictException)?.Port ?? (ShowsStartFailure(s) ? s.StartFailure!.ConflictPort : null);

    private static bool AllRunning(GridStatus s) => Parts(s).All(p => p.C.State == ProcessState.Running);

    private static bool InTransition(GridStatus s) =>
        s.Activity != GridActivity.None || Parts(s).Any(p => p.C.State is ProcessState.Starting or ProcessState.Stopping);

    private static IEnumerable<(ComponentStatus C, string Message)> Parts(GridStatus s)
    {
        yield return (s.Database, "The database isn't running.");
        yield return (s.Robust, "The grid service isn't running.");
        if (s.Region is { } r) yield return (r, "Your region isn't running.");
    }

    /// <summary>The line shown when there is no problem.</summary>
    public static string Headline(GridStatus s)
    {
        var states = Parts(s).Select(p => p.C.State).ToList();
        if (states.All(x => x == ProcessState.Running)) return "Your world is running.";
        switch (s.Activity)
        {
            case GridActivity.Starting: return "Your world is starting.";
            case GridActivity.Stopping: return "Your world is stopping.";
            case GridActivity.Restarting: return "Your world is restarting.";
        }
        if (states.Any(x => x == ProcessState.Starting)) return "Your world is starting.";
        if (states.Any(x => x == ProcessState.Stopping)) return "Your world is stopping.";
        return "Your world is stopped.";
    }

    private static ComponentView View(ComponentStatus c, string label) =>
        new(c.Name, label, c.State.ToString(), StateText(c.State), Detail(c));

    private static string StateText(ProcessState state) => state switch
    {
        ProcessState.Crashed => "Restarting after a crash",
        _ => state.ToString(),
    };

    private static string Detail(ComponentStatus c)
    {
        var parts = new List<string>();
        if (c.State == ProcessState.Running && c.Uptime is { } up) parts.Add("up " + Uptime(up));
        if (c.State == ProcessState.Running && c.MemoryBytes is { } bytes) parts.Add(Memory(bytes));
        return string.Join(" · ", parts);
    }

    private static string Uptime(TimeSpan t) =>
        t.TotalMinutes < 1 ? "less than a minute"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : $"{(int)t.TotalDays} d {t.Hours} h";

    private static string Memory(long bytes)
    {
        var mb = bytes / (1024.0 * 1024);
        return mb < 1024 ? $"{mb:0} MB" : $"{mb / 1024:0.0} GB";
    }
}
