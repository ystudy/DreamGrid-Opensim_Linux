using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Tickets;

/// <summary>
/// The support tickets (design 2026-11-05-support-tickets, "Storage"): one JSON file per ticket, <c>&lt;root&gt;/&lt;id&gt;.json</c>,
/// written atomically (owner-only on Linux), and every ticket held in memory, which is the list's index; it is rebuilt from the files at
/// start, so there's no separate index to go wrong. Every write happens under one lock, so two people answering at once never
/// overwrite each other. Safe from any thread.
/// </summary>
public sealed class TicketStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly ILogger<TicketStore>? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Ticket> _tickets = [];

    public TicketStore(string root, ILogger<TicketStore>? log = null)
    {
        _root = root;
        _log = log;
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.GetFiles(root, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Ticket>(File.ReadAllText(file), Options) is { Number.Length: > 0 } t) _tickets[t.Id] = t;
                else throw new JsonException("empty");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                log?.LogWarning("Tickets: {Path} could not be read ({Error}); it is left where it is.", file, e.GetType().Name);
            }
        }
    }

    /// <summary>Every ticket, newest activity first.</summary>
    public IReadOnlyList<Ticket> All()
    {
        lock (_gate) return [.. _tickets.Values.OrderByDescending(t => t.LastActivity)];
    }

    public Ticket? Get(Guid id)
    {
        lock (_gate) return _tickets.GetValueOrDefault(id);
    }

    public Ticket? ByNumber(string number)
    {
        lock (_gate) return _tickets.Values.FirstOrDefault(t => string.Equals(t.Number, number, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Makes a ticket with the next number (<c>T-0001</c>, counting up from the highest there is) from <paramref name="build"/>, which
    /// gets the id and number, and saves it. <paramref name="allowed"/> sees every ticket under the same lock (for the limits) and can
    /// refuse; null when refused or when it couldn't be written.
    /// </summary>
    public Ticket? Create(Func<IReadOnlyCollection<Ticket>, bool> allowed, Func<Guid, string, Ticket> build)
    {
        lock (_gate)
        {
            if (!allowed(_tickets.Values)) return null;
            var next = _tickets.Values.Select(t => Count(t.Number)).DefaultIfEmpty(0).Max() + 1;
            var ticket = build(Guid.NewGuid(), "T-" + next.ToString("D4", CultureInfo.InvariantCulture));
            return TryWrite(ticket) ? _tickets[ticket.Id] = ticket : null;
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the ticket as it is now and saves it, under the lock. <paramref name="change"/> returns
    /// null to change nothing. The saved ticket, or null when there's no such ticket, nothing changed, or it couldn't be written.
    /// </summary>
    public Ticket? Update(Guid id, Func<Ticket, Ticket?> change)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(id, out var now) || change(now) is not { } next) return null;
            if (!TryWrite(next)) return null;
            return _tickets[id] = next;
        }
    }

    /// <summary>
    /// Like <see cref="Update(Guid, Func{Ticket, Ticket?})"/>, but <paramref name="change"/> also sees every ticket under the same
    /// lock, so a limit across tickets is checked at the moment of the change.
    /// </summary>
    public Ticket? Update(Guid id, Func<IReadOnlyCollection<Ticket>, Ticket, Ticket?> change) =>
        Update(id, t => change(_tickets.Values, t));

    /// <summary>Deletes every ticket <paramref name="drop"/> picks; how many were deleted.</summary>
    public int DeleteWhere(Func<Ticket, bool> drop)
    {
        lock (_gate)
        {
            var gone = 0;
            foreach (var t in _tickets.Values.Where(drop).ToList())
                if (Delete(t.Id)) gone++;
            return gone;
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            if (!_tickets.ContainsKey(id)) return false;
            try
            {
                File.Delete(PathOf(id));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log?.LogWarning("Tickets: {Id} could not be deleted ({Error}).", id, e.GetType().Name);
                return false;
            }
            return _tickets.Remove(id);
        }
    }

    private bool TryWrite(Ticket t)
    {
        try
        {
            Directory.CreateDirectory(_root);
            AtomicFile.WriteAllText(PathOf(t.Id), JsonSerializer.Serialize(t, Options));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning("Tickets: {Number} could not be saved ({Error}).", t.Number, e.GetType().Name);
            return false;
        }
    }

    private string PathOf(Guid id) => Path.Combine(_root, id.ToString("N") + ".json");

    private static int Count(string number) => number.Length > 2 && int.TryParse(number.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
