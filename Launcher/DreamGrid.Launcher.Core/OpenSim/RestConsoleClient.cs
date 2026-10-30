using System.Diagnostics;
using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.OpenSim;

/// <summary>A console output line; <see cref="IsPrompt"/> marks an interactive question (Prompt="true", Command="false").</summary>
public sealed record ConsoleOutput(string Text, bool IsPrompt);

/// <summary>The REST console rejected the USER/PASS (HTTP 401/403 or no SessionID).</summary>
public sealed class ConsoleAuthException(string message) : Exception(message);

/// <summary>
/// The console accepted the command (its POST got a success reply), then collecting the output failed: the session expired, a
/// read failed, or Robust stopped answering. The command may have run. An <see cref="HttpRequestException"/>, so callers that
/// treat any console failure alike keep working; a caller that must not assume "nothing happened" catches this first.
/// </summary>
public sealed class ConsoleCommandSentException(string message, Exception? inner = null) : HttpRequestException(message, inner);

/// <summary>
/// Client for OpenSim's REST console (RemoteConsole.cs): form-encoded POSTs to
/// /StartSession/, /SessionCommand/ and /ReadResponses/&lt;id&gt;/. One session is cached and reused.
/// </summary>
/// <remarks>
/// Server behaviour this client accounts for:
/// <list type="bullet">
/// <item>A new session is sent the console scrollback (up to 1000 lines) on its first read. Every session this
/// client creates (first use, or restart after a 401/404) has that scrollback read and discarded before a
/// command is sent.</item>
/// <item>The server reports an unknown or expired session as HTTP 404 (/SessionCommand/ and /CloseSession/ with
/// an ID missing from its connection table; sessions are dropped after ~500 s without a read, and the
/// session's /ReadResponses/ handler is removed with it). 401 is what /StartSession/ answers to bad
/// credentials; a 401 on read or send is treated as expiry too. A dropped session whose read handler is
/// still registered answers an empty reply, which cannot be told apart from "no output". A call that sees
/// expiry before or at the POST reopens the session and sends the command once more (a 404 on /SessionCommand/ is
/// answered before any input is queued, so the command did not run); a second expiry throws. Any failure on a read after
/// the POST was accepted (expiry, an error reply, a lost connection, an HttpClient timeout) throws
/// <see cref="ConsoleCommandSentException"/> and never resends, as the command may already have run.</item>
/// <item>/ReadResponses/ is a long poll: with no pending lines the server holds the request for ~25 s, and it
/// advances the session's "last line seen" marker when it builds the reply. A read the client cancelled could
/// therefore swallow lines, so reads are never cancelled. The client only stops waiting: the read stays
/// outstanding and its reply is consumed by the next call (stale replies that completed between calls are
/// discarded). Each call is bounded by <c>settle</c>, not by the poll.</item>
/// <item>The server can drop a read without ever answering it: GetEvents throws when a line is written while it builds
/// the reply (the line counter is bumped outside the scrollback lock) and the poll manager swallows the exception.
/// The session's lines stay queued, but only a new read gets them. A read still unanswered after
/// <see cref="LostReadAfter"/> (past the server's 25 s poll limit) is therefore no longer waited for and a new one is
/// made; the old one is still not cancelled.</item>
/// <item>Input="true" marks the echo of typed text, Command="true" the command prompt (both dropped by
/// <see cref="RunAsync"/>); Prompt="true" with Command="false" is an interactive question (kept).</item>
/// </list>
/// </remarks>
public sealed class RestConsoleClient(HttpClient http, Uri baseUri, string user, string password,
    ILogger<RestConsoleClient>? log = null)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LateOutputWait = TimeSpan.FromMilliseconds(100);
    private const int MaxSettleMultiple = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionId;
    private Task<List<ConsoleLine>>? _pendingRead;
    private long _pendingReadStarted; // Stopwatch timestamp

    /// <summary>
    /// How long a read may stay unanswered before it counts as dropped by the server. The server answers every long
    /// poll within ~25 s ("no events" on timeout), so a read older than this will never be answered.
    /// </summary>
    internal TimeSpan LostReadAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True when /StartSession/ answers with a SessionID. Probes with a throwaway session that is closed
    /// again, so polling does not leak server-side sessions. A rejected password throws
    /// <see cref="ConsoleAuthException"/>.
    /// </summary>
    public async Task<bool> IsUpAsync(CancellationToken ct)
    {
        try
        {
            var probe = await StartSessionAsync(ct);
            try { using var _ = await http.PostAsync(Url("CloseSession"), Form(("ID", probe)), ct); }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // the session opened: it is up
            return true;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>Sends <paramref name="command"/> without reading the reply.</summary>
    public async Task SendAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var fresh = await EnsureSessionAsync(ct);
                    if (!fresh) await DrainLateOutputAsync(ct);
                    await PostCommandAsync(command, ct);
                    return;
                }
                catch (SessionExpiredException) when (attempt == 0) { _sessionId = null; }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Sends <paramref name="command"/> and collects output until no new line has arrived for
    /// <paramref name="settle"/> (total wait capped at 20 x settle). The echo of the typed command and the
    /// command prompt are dropped; interactive prompt lines are kept.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAsync(string command, TimeSpan settle, CancellationToken ct) =>
        (await RunLinesAsync(command, settle, ct)).Select(l => l.Text).ToList();

    /// <summary>As <see cref="RunAsync"/>, keeping whether each line is an interactive prompt.</summary>
    public async Task<IReadOnlyList<ConsoleOutput>> RunLinesAsync(string command, TimeSpan settle, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var fresh = await EnsureSessionAsync(ct);
                    if (!fresh) await DrainLateOutputAsync(ct);
                    await PostCommandAsync(command, ct);
                    break;
                }
                catch (SessionExpiredException) when (attempt == 0) { _sessionId = null; }
            }
            // The server accepted the command: never resend it. A failure from here on loses only the output, and says so
            // (ConsoleCommandSentException), since the command may have run. Only the caller's own cancellation stays one.
            try { return await CollectAsync(settle, ct); }
            catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                var what = e is SessionExpiredException ? "session expired" : "stopped answering";
                throw new ConsoleCommandSentException(
                    $"REST console {what} while waiting for the output of '{Verb(command)}'; the command was sent, its output was lost.", e);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<ConsoleOutput>> CollectAsync(TimeSpan settle, CancellationToken ct)
    {
        var result = new List<ConsoleOutput>();
        var clock = Stopwatch.StartNew();
        var lastNew = TimeSpan.Zero;
        var cap = settle * MaxSettleMultiple;
        while (true)
        {
            var wait = Min(settle - (clock.Elapsed - lastNew), cap - clock.Elapsed);
            if (wait <= TimeSpan.Zero) break;
            var lines = await TryReadAsync(wait, ct);
            if (lines is null) break; // nothing arrived in time; the read stays outstanding
            if (lines.Count > 0)
            {
                lastNew = clock.Elapsed;
                result.AddRange(lines.Where(l => !l.IsInput && !l.IsCommand).Select(l => new ConsoleOutput(l.Text, l.IsPrompt)));
            }
            else
            {
                await Task.Delay(PollInterval, ct);
            }
        }
        return result;
    }

    /// <summary>
    /// Discards output that arrived after an earlier call stopped waiting, so it is not attributed to the
    /// command about to be sent. Output lives in the outstanding read, or is queued on the server for the next
    /// one, so at least one bounded read is made (starting one if none is outstanding) and repeated while it
    /// returns lines. Like every read, it is never cancelled.
    /// </summary>
    private async Task DrainLateOutputAsync(CancellationToken ct)
    {
        while (await TryReadAsync(LateOutputWait, ct) is { Count: > 0 } late)
            LogLate(late);
    }

    private void LogLate(List<ConsoleLine> lines)
    {
        if (log is null) return;
        foreach (var l in lines)
        {
            // An echo repeats a typed command, which can carry a password: name the command only.
            if (l.IsInput) log.LogDebug("Discarding late console echo of '{Command}'", Verb(l.Text));
            else log.LogDebug("Discarding late console output: {Line}", l.Text);
        }
    }

    /// <summary>Forgets the cached session and tells the server to drop it (best effort).</summary>
    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var id = _sessionId;
            _sessionId = null;
            DropPendingRead();
            if (id is null) return;
            try { using var _ = await http.PostAsync(Url("CloseSession"), Form(("ID", id))); }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
        }
        finally { _gate.Release(); }
    }

    /// <summary>The first two words of a command, enough to name it without echoing arguments such as passwords.</summary>
    private static string Verb(string command) => string.Join(' ', command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries).Take(2));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private Uri Url(string action) => new(baseUri, "/" + action + "/");

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private async Task<string> StartSessionAsync(CancellationToken ct)
    {
        using var resp = await http.PostAsync(Url("StartSession"), Form(("USER", user), ("PASS", password)), ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ConsoleAuthException($"REST console rejected the credentials (HTTP {(int)resp.StatusCode}).");
        resp.EnsureSuccessStatusCode();

        var sid = ParseXml(await resp.Content.ReadAsStringAsync(ct))?.Descendants("SessionID").FirstOrDefault()?.Value.Trim();
        if (string.IsNullOrEmpty(sid))
            throw new ConsoleAuthException("REST console returned no SessionID.");
        return sid;
    }

    /// <summary>
    /// Makes sure a session exists; true when a new one was started. A new one has its scrollback drained
    /// before first use.
    /// </summary>
    private async Task<bool> EnsureSessionAsync(CancellationToken ct)
    {
        if (_pendingRead is { IsCompleted: true } done)
        {
            // Reply that arrived between calls: stale output, or a dead session.
            _pendingRead = null;
            try { LogLate(await done); }
            catch (SessionExpiredException) { _sessionId = null; }
            catch (HttpRequestException) { }
        }
        if (_sessionId is not null) return false;
        await StartFreshSessionAsync(ct);
        return true;
    }

    /// <summary>
    /// Starts a session and discards the scrollback the server sends on its first read. With an empty
    /// scrollback the server holds that read open, so the wait is bounded; the read then stays
    /// outstanding and carries the next real output.
    /// </summary>
    private async Task StartFreshSessionAsync(CancellationToken ct)
    {
        DropPendingRead();
        _sessionId = await StartSessionAsync(ct);
        await TryReadAsync(DrainWait, ct);
    }

    private void DropPendingRead()
    {
        var t = _pendingRead;
        _pendingRead = null;
        t?.ContinueWith(x => _ = x.Exception, TaskScheduler.Default); // observe and ignore
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the outstanding read (starting one if needed); null when it
    /// did not answer in time. The read itself is never cancelled.
    /// </summary>
    private async Task<List<ConsoleLine>?> TryReadAsync(TimeSpan wait, CancellationToken ct)
    {
        var id = _sessionId ?? throw new InvalidOperationException("No console session.");
        var deadline = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (_pendingRead is null)
            {
                _pendingRead = ReadOnceAsync(id);
                _pendingReadStarted = Stopwatch.GetTimestamp();
            }
            var read = _pendingRead;
            var untilLost = LostReadAfter - Stopwatch.GetElapsedTime(_pendingReadStarted);
            var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            var slice = Min(left, untilLost);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var first = slice > TimeSpan.Zero ? await Task.WhenAny(read, Task.Delay(slice, timer.Token)) : null;
            ct.ThrowIfCancellationRequested();
            if (first == read || read.IsCompleted)
            {
                timer.Cancel();
                _pendingRead = null;
                try { return await read; }
                catch (SessionExpiredException) { _sessionId = null; throw; }
            }
            if (Stopwatch.GetElapsedTime(_pendingReadStarted) < LostReadAfter) return null; // nothing in time; the read stays outstanding
            AbandonLostRead(read);
            if (Stopwatch.GetTimestamp() >= deadline) return null; // the next call reads again
        }
    }

    /// <summary>
    /// Stops waiting for a read the server has dropped (RemoteConsole.GetEvents can throw when a console line is written
    /// while it builds the reply, and the poll manager swallows that without answering). The read is not cancelled,
    /// only no longer waited for; the server did not mark its lines as seen, so the next read returns them. Should it
    /// answer after all, its lines are logged as late output.
    /// </summary>
    private void AbandonLostRead(Task<List<ConsoleLine>> read)
    {
        _pendingRead = null;
        log?.LogWarning("The REST console at {Uri} left a read unanswered for {Seconds:0} s; reading again.",
            baseUri, LostReadAfter.TotalSeconds);
        read.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) LogLate(t.Result);
            else _ = t.Exception;
        }, TaskScheduler.Default);
    }

    private async Task<List<ConsoleLine>> ReadOnceAsync(string id)
    {
        using var resp = await http.PostAsync(new Uri(baseUri, "/ReadResponses/" + id + "/"), Form(("ID", id)));
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            throw new SessionExpiredException();
        resp.EnsureSuccessStatusCode();
        var doc = ParseXml(await resp.Content.ReadAsStringAsync());
        if (doc is null) return new List<ConsoleLine>();
        return doc.Descendants("Line")
            .Select(e => new ConsoleLine(e.Value, IsTrue(e, "Command"), IsTrue(e, "Input"), IsTrue(e, "Prompt")))
            .ToList();
    }

    private static bool IsTrue(XElement e, string attr) =>
        string.Equals((string?)e.Attribute(attr), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>POSTs the command; 401/404 (session expired) throws <see cref="SessionExpiredException"/>.</summary>
    private async Task PostCommandAsync(string command, CancellationToken ct)
    {
        using var resp = await http.PostAsync(Url("SessionCommand"), Form(("ID", _sessionId!), ("COMMAND", command)), ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            throw new SessionExpiredException();
        resp.EnsureSuccessStatusCode();
    }

    private static XElement? ParseXml(string text)
    {
        try { return XDocument.Parse(text).Root; }
        catch (System.Xml.XmlException) { return null; }
    }

    private sealed class SessionExpiredException(string message = "REST console session expired.") : HttpRequestException(message);

    private sealed record ConsoleLine(string Text, bool IsCommand, bool IsInput, bool IsPrompt);
}
