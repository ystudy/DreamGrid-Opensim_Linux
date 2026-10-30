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

/// <summary>
/// The audit log: one JSON object per line in <c>audit.log</c>, rolled by size like the launcher log (<see cref="RollingFileWriter"/>).
/// Safe to call from any thread. A line that cannot be written is lost, never an exception into the request.
/// </summary>
public sealed class AuditLog(string path, TimeProvider time, long maxBytes = 10 * 1024 * 1024, int files = 5) : IDisposable
{
    private readonly RollingFileWriter _file = new(path, maxBytes, files);

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
