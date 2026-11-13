using System.Text.Json;

namespace GridGate.Launcher.Core.Tickets;

/// <summary>
/// Who switched on the "New support tickets" email (design 2026-11-05-support-tickets, "Notices"), and for each of them when their last
/// one went out and how far it covered. Off by default: only staff who switched it on are listed. Kept in <c>file</c>
/// (<c>&lt;data&gt;/guests/ticket-notices.json</c>, written atomically): account ids and times only, never a ticket's text, since the
/// tickets themselves say what is new. A missing or damaged file reads as nobody switched on. Safe from any thread.
/// </summary>
public sealed class TicketNotices(string file)
{
    /// <param name="LastSent">When the last email went out to them, or null if none yet.</param>
    /// <param name="Covered">Everything up to this time is already told (or came before they switched on).</param>
    public sealed record Entry(DateTimeOffset? LastSent, DateTimeOffset Covered);

    private sealed record State(Dictionary<Guid, Entry> On);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();

    public bool IsOn(Guid staff)
    {
        lock (_gate) return Read().On.ContainsKey(staff);
    }

    /// <summary>
    /// Switches the email on or off for <paramref name="staff"/>. Switching on counts from <paramref name="now"/>: nothing from
    /// before is sent. Switching on again when already on changes nothing.
    /// </summary>
    public void Set(Guid staff, bool on, DateTimeOffset now)
    {
        lock (_gate)
        {
            var s = Read();
            if (on == s.On.ContainsKey(staff)) return;
            if (on) s.On[staff] = new Entry(null, now);
            else s.On.Remove(staff);
            Write(s);
        }
    }

    /// <summary>Everyone who has it on.</summary>
    public IReadOnlyDictionary<Guid, Entry> All()
    {
        lock (_gate) return Read().On;
    }

    /// <summary>An email went out to <paramref name="staff"/> at <paramref name="at"/>, telling everything up to <paramref name="covered"/>.</summary>
    public void Sent(Guid staff, DateTimeOffset at, DateTimeOffset covered)
    {
        lock (_gate)
        {
            var s = Read();
            // Switched off meanwhile: stay off.
            if (!s.On.ContainsKey(staff)) return;
            s.On[staff] = new Entry(at, covered);
            Write(s);
        }
    }

    /// <summary>Nothing to send <paramref name="staff"/> up to <paramref name="covered"/> (they have no address): it isn't kept for later.</summary>
    public void Skip(Guid staff, DateTimeOffset covered)
    {
        lock (_gate)
        {
            var s = Read();
            if (!s.On.TryGetValue(staff, out var e)) return;
            s.On[staff] = e with { Covered = covered };
            Write(s);
        }
    }

    private State Read()
    {
        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(file), Options) is { On: not null } s ? s : Empty();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            return Empty();
        }
    }

    private static State Empty() => new([]);

    private void Write(State s)
    {
        AtomicFile.WriteAllTextCreatingFolder(file, JsonSerializer.Serialize(s, Options));
    }
}
