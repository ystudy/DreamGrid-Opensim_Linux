using System.Text.Json;

namespace DreamGrid.Launcher.Core.Logs;

/// <summary>
/// One thing a person with a role did (or tried to do) on this computer's website or port-8001 API.
/// Never a password, code, secret, MachineId or token: the caller passes names, not what was typed.
/// </summary>
/// <param name="Actor">The avatar's name ("First Last"), the first name only for a refused sign-in, or "(an incomplete name)" when no last name was typed (the rest could be a password typed into the name field), except a staff member whose password was right but whose two-factor file can't be read or who typed a wrong two-factor code (the full name), or the word MachineId.</param>
/// <param name="ActorId">The avatar's principal id, when there is one.</param>
/// <param name="Role">owner, admin, supervisor, or machine.</param>
/// <param name="Action">One of the names the plan lists: sign-in, request, command and so on.</param>
/// <param name="Target">What it was done to ("POST /dashboard", a command name); never a query string.</param>
/// <param name="Result">A status code, ACK or NAK, ok or refused.</param>
public sealed record AuditEntry(string Actor, Guid? ActorId, string Role, string? Ip, string Action, string? Target, string Result);

/// <summary>An audit line read back, with when it was written.</summary>
public sealed record AuditEntryRead(AuditEntry Entry, DateTimeOffset At);

/// <summary>
/// The audit log: one JSON object per line in <c>audit.log</c>, rolled by size like the launcher log (<see cref="RollingFileWriter"/>).
/// Safe to call from any thread. A line that cannot be written is lost, never an exception into the request.
/// </summary>
public sealed class AuditLog(string path, TimeProvider time, long maxBytes = 10 * 1024 * 1024, int files = 5) : IDisposable
{
    private readonly RollingFileWriter _file = new(path, maxBytes, files);

    /// <summary>How much of the log <see cref="Recent"/> reads at most, newest first.</summary>
    public const long RecentBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The newest lines (at most <paramref name="max"/>) that <paramref name="match"/> takes, newest first, from <c>audit.log</c>
    /// and then the rolled files, reading no more than <see cref="RecentBytes"/> in all. A line that isn't readable is passed over.
    /// </summary>
    public IReadOnlyList<AuditEntryRead> Recent(Func<AuditEntryRead, bool> match, int max)
    {
        var found = new List<AuditEntryRead>();
        var budget = RecentBytes;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 0; i < files && found.Count < max && budget > 0; i++)
        {
            var file = Path.Combine(dir, i == 0 ? name + ext : $"{name}.{i}{ext}");
            string text;
            try
            {
                using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var take = Math.Min(s.Length, budget);
                s.Seek(-take, SeekOrigin.End);
                budget -= take;
                text = new StreamReader(s).ReadToEnd();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                if (Parse(line) is not { } read || !match(read)) continue;
                found.Add(read);
                if (found.Count == max) break;
            }
        }
        return found;
    }

    private static AuditEntryRead? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? Text(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (!DateTimeOffset.TryParse(Text("time"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) return null;
            Guid? id = Guid.TryParse(Text("actorId"), out var g) ? g : null;
            return new AuditEntryRead(new AuditEntry(Text("actor") ?? "", id, Text("role") ?? "", Text("ip"), Text("action") ?? "",
                Text("target"), Text("result") ?? ""), at);
        }
        catch (JsonException)
        {
            // A line cut at the read's start, or a damaged one.
            return null;
        }
    }

    public void Write(AuditEntry e)
    {
        try
        {
            // Utf8JsonWriter escapes every control character, so a field can never start a second line.
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("time", time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
                json.WriteString("actor", e.Actor);
                if (e.ActorId is { } id) json.WriteString("actorId", id); else json.WriteNull("actorId");
                json.WriteString("role", e.Role);
                json.WriteString("ip", e.Ip);
                json.WriteString("action", e.Action);
                json.WriteString("target", e.Target);
                json.WriteString("result", e.Result);
                json.WriteEndObject();
            }
            _file.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        catch (Exception)
        {
            // An audit line never takes a request down.
        }
    }

    public void Dispose() => _file.Dispose();
}
