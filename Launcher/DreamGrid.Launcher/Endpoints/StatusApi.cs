using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Antiforgery;

namespace DreamGrid.Launcher.Endpoints;

/// <param name="Name">"database" or "robust".</param>
/// <param name="Label">What the dashboard calls it: "Database", "Grid service".</param>
/// <param name="Detail">Uptime and memory in words, or "".</param>
public sealed record ComponentView(string Name, string Label, string State, string StateText, string Detail);

/// <param name="RunsWith">The other regions in its program; null for anonymous callers, who get names and states only.</param>
/// <param name="LoginsEnabled">Null for anonymous callers.</param>
public sealed record RegionView(string Name, string State, string StateText, IReadOnlyList<string>? RunsWith, bool? LoginsEnabled);

public sealed record GridView(ComponentView Database, ComponentView Robust, IReadOnlyList<RegionView> Regions, bool LoginsEnabled, string LoginUri);

/// <param name="Admin">True when the caller is signed in as an admin; the dashboard reloads to sign in when it turns false.</param>
/// <summary>What the dashboard shows and <c>GET /api/status</c> returns: names and states only, no paths or secrets.</summary>
public sealed record StatusView(GridView Grid, IReadOnlyList<string> Online, bool OnlineAvailable, string? Problem, string Headline, bool Admin);

public static class StatusApi
{
    public static void Map(WebApplication app)
    {
        // Open to anyone (the port-8001 contract already tells the LAN the region status); avatar names only for an admin.
        app.MapGet("/api/status", async (HttpContext ctx, IGridController grid, IOnlineUsers online) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(await BuildAsync(grid, online, ctx.User.IsInRole(AuthSchemes.AdminRole), ctx.RequestAborted));
        }).AllowAnonymous();

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
        var regionNames = s.Regions.ToDictionary(r => r.Id, r => r.Name);
        return new StatusView(
            new GridView(View(s.Database, "Database"), View(s.Robust, "Grid service"),
                s.Regions.Select(r => new RegionView(r.Name, r.State.ToString(), RegionStateText(r.State),
                    withOnline ? r.RunsWith : null, withOnline ? r.LoginsEnabled : null)).ToList(),
                s.LoginsEnabled, s.LoginUri),
            people.Avatars.Select(a => regionNames.TryGetValue(a.RegionId, out var region) ? $"{a.Name} in {region}" : a.Name).ToList(),
            people.Available, problem, problem ?? Headline(s), withOnline);
    }

    public const string CouldNotStart = "Your world could not start.";

    /// <summary>
    /// In plain words: the last failed start (unless everything runs again), else the first component (in start
    /// order) that failed or crashed, else (with nothing under way) one that is stopped while another runs; null when
    /// the world is running, starting, stopping, restarting or wholly stopped. A region stopped or parked on purpose
    /// is not a problem (spec 6.8); a failed one is, by name.
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

    /// <summary>Everything is up: the parts, and every region that isn't parked (a stopped one too: a start that left it stopped failed).</summary>
    private static bool AllRunning(GridStatus s) =>
        Parts(s).All(p => p.C.State == ProcessState.Running) && s.Regions.All(r => r.State is RegionState.Running or RegionState.Parked);

    private static bool InTransition(GridStatus s) =>
        s.Activity != GridActivity.None || Parts(s).Any(p => p.C.State is ProcessState.Starting or ProcessState.Stopping);

    /// <summary>
    /// The database, Robust, and every region that should be up (not stopped or parked on purpose), each with its state
    /// as a process state and the sentence for when it is down.
    /// </summary>
    private static IEnumerable<(ComponentStatus C, string Message)> Parts(GridStatus s)
    {
        yield return (s.Database, "The database isn't running.");
        yield return (s.Robust, "The grid service isn't running.");
        foreach (var r in s.Regions.Where(r => r.State is not (RegionState.Stopped or RegionState.Parked)))
            yield return (new ComponentStatus(r.Name, AsProcessState(r.State), null, null, null, null), $"{r.Name} isn't running.");
    }

    private static ProcessState AsProcessState(RegionState state) => state switch
    {
        RegionState.Running => ProcessState.Running,
        RegionState.Starting => ProcessState.Starting,
        RegionState.Stopping => ProcessState.Stopping,
        RegionState.Failed => ProcessState.Failed,
        _ => ProcessState.Stopped,
    };

    /// <summary>The line shown when there is no problem.</summary>
    public static string Headline(GridStatus s)
    {
        // What is under way comes first: with many regions, everything else can be running meanwhile.
        switch (s.Activity)
        {
            case GridActivity.Starting: return "Your world is starting.";
            case GridActivity.Stopping: return "Your world is stopping.";
            case GridActivity.Restarting: return "Your world is restarting.";
        }
        var states = Parts(s).Select(p => p.C.State).ToList();
        if (states.All(x => x == ProcessState.Running)) return "Your world is running.";
        if (states.Any(x => x == ProcessState.Starting)) return "Your world is starting.";
        if (states.Any(x => x == ProcessState.Stopping)) return "Your world is stopping.";
        return "Your world is stopped.";
    }

    private static ComponentView View(ComponentStatus c, string label) =>
        new(c.Name, label, c.State.ToString(), StateText(c.State), Detail(c));

    /// <summary>The dashboard's word for a region's state ("Parked" for a parked one); the Regions page uses it too.</summary>
    public static string RegionStateText(RegionState state) => state.ToString();

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
