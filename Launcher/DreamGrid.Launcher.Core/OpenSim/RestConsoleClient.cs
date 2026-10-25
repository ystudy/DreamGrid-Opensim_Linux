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
public sealed class RestConsoleClient(HttpClient http, Uri baseUri, string user, string password)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxSettleMultiple = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionId;

    /// <summary>True when /StartSession/ answers with a SessionID.</summary>
    public async Task<bool> IsUpAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _sessionId = await StartSessionAsync(ct);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or ConsoleAuthException
                                      or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Sends <paramref name="command"/> without reading the reply.</summary>
    public async Task SendAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await PostSessionAsync("SessionCommand", command, ct); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Sends <paramref name="command"/> and collects output until no new line has arrived for
    /// <paramref name="settle"/> (total wait capped at 20 x settle). Echoed command lines are dropped.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAsync(string command, TimeSpan settle, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_sessionId is null)
            {
                _sessionId = await StartSessionAsync(ct);
                // A new session is sent the console scrollback first; discard it.
                await ReadLinesAsync(ct);
            }

            await PostSessionAsync("SessionCommand", command, ct);

            var result = new List<string>();
            var clock = Stopwatch.StartNew();
            var lastNew = clock.Elapsed;
            var cap = settle * MaxSettleMultiple;
            while (true)
            {
                await Task.Delay(PollInterval, ct);
                var lines = await ReadLinesAsync(ct);
                if (lines.Count > 0)
                {
                    lastNew = clock.Elapsed;
                    result.AddRange(lines.Where(l => !l.IsCommand).Select(l => l.Text));
                }
                if (clock.Elapsed - lastNew >= settle || clock.Elapsed >= cap) break;
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
            if (id is null) return;
            try { using var _ = await http.PostAsync(Url("CloseSession"), Form(("ID", id))); }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
        }
        finally { _gate.Release(); }
    }

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
    /// POSTs to a session endpoint. On 401/404 (session expired) starts a new session and retries once.
    /// </summary>
    private async Task<string> SessionRequestAsync(Func<string, FormUrlEncodedContent> body, Func<string, Uri> url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            _sessionId ??= await StartSessionAsync(ct);
            using var resp = await http.PostAsync(url(_sessionId), body(_sessionId), ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound && attempt == 0)
            {
                _sessionId = null;
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }

    private Task<string> PostSessionAsync(string action, string command, CancellationToken ct) =>
        SessionRequestAsync(id => Form(("ID", id), ("COMMAND", command)), _ => Url(action), ct);

    private async Task<List<ConsoleLine>> ReadLinesAsync(CancellationToken ct)
    {
        var xml = await SessionRequestAsync(id => Form(("ID", id)),
            id => new Uri(baseUri, "/ReadResponses/" + id + "/"), ct);
        var doc = ParseXml(xml);
        if (doc is null) return new List<ConsoleLine>();
        return doc.Descendants("Line")
            .Select(e => new ConsoleLine(e.Value, string.Equals((string?)e.Attribute("Command"), "true", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static XElement? ParseXml(string text)
    {
        try { return XDocument.Parse(text).Root; }
        catch (System.Xml.XmlException) { return null; }
    }

    private sealed record ConsoleLine(string Text, bool IsCommand);
}
