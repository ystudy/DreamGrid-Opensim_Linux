using System.Diagnostics;
using System.Net;
using System.Xml.Linq;

namespace DreamGrid.Launcher.Core.OpenSim;

/// <summary>The REST console rejected the USER/PASS (HTTP 401/403 or no SessionID).</summary>
public sealed class ConsoleAuthException(string message) : Exception(message);

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
/// <item>/ReadResponses/ is a long poll: with no pending lines the server holds the request for ~25 s, and it
/// advances the session's "last line seen" marker when it builds the reply. A read the client cancelled could
/// therefore swallow lines, so reads are never cancelled. The client only stops waiting: the read stays
/// outstanding and its reply is consumed by the next call (stale replies that completed between calls are
/// discarded). Each call is bounded by <c>settle</c>, not by the poll.</item>
/// <item>Input="true" marks the echo of typed text, Command="true" the command prompt (both dropped by
/// <see cref="RunAsync"/>); Prompt="true" with Command="false" is an interactive question (kept).</item>
/// </list>
/// </remarks>
public sealed class RestConsoleClient(HttpClient http, Uri baseUri, string user, string password)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(1);
    private const int MaxSettleMultiple = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionId;
    private Task<List<ConsoleLine>>? _pendingRead;

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
            await EnsureSessionAsync(ct);
            await PostCommandAsync(command, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Sends <paramref name="command"/> and collects output until no new line has arrived for
    /// <paramref name="settle"/> (total wait capped at 20 x settle). The echo of the typed command and the
    /// command prompt are dropped; interactive prompt lines are kept.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAsync(string command, TimeSpan settle, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureSessionAsync(ct);
            await PostCommandAsync(command, ct);

            var result = new List<string>();
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
                    result.AddRange(lines.Where(l => !l.IsInput && !l.IsCommand).Select(l => l.Text));
                }
                else
                {
                    await Task.Delay(PollInterval, ct);
                }
            }
            return result;
        }
        finally { _gate.Release(); }
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

    /// <summary>Makes sure a session exists. A new one has its scrollback drained before first use.</summary>
    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_pendingRead is { IsCompleted: true } done)
        {
            // Reply that arrived between calls: stale output, or a dead session.
            _pendingRead = null;
            try { await done; }
            catch (SessionExpiredException) { _sessionId = null; }
            catch (HttpRequestException) { }
        }
        if (_sessionId is null) await StartFreshSessionAsync(ct);
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
        var read = _pendingRead ??= ReadOnceAsync(id);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = await Task.WhenAny(read, Task.Delay(wait, timer.Token));
        ct.ThrowIfCancellationRequested();
        if (first != read) return null;
        timer.Cancel();
        _pendingRead = null;
        try { return await read; }
        catch (SessionExpiredException) { _sessionId = null; throw; }
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
            .Select(e => new ConsoleLine(e.Value, IsTrue(e, "Command"), IsTrue(e, "Input")))
            .ToList();
    }

    private static bool IsTrue(XElement e, string attr) =>
        string.Equals((string?)e.Attribute(attr), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>POSTs the command. On 401/404 (session expired) starts a new session and retries once.</summary>
    private async Task PostCommandAsync(string command, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var resp = await http.PostAsync(Url("SessionCommand"), Form(("ID", _sessionId!), ("COMMAND", command)), ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound && attempt == 0)
            {
                await StartFreshSessionAsync(ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return;
        }
    }

    private static XElement? ParseXml(string text)
    {
        try { return XDocument.Parse(text).Root; }
        catch (System.Xml.XmlException) { return null; }
    }

    private sealed class SessionExpiredException() : HttpRequestException("REST console session expired.");

    private sealed record ConsoleLine(string Text, bool IsCommand, bool IsInput);
}
