using System.Security.Cryptography;

namespace DreamGrid.Launcher.Core.Tickets;

/// <summary>
/// A ticket's private links (design 2026-11-05-support-tickets, "After sending"). Each is 256 random bits, given out once as
/// base64url; only its SHA-256 is kept, in the ticket, with an expiry. Unlike <c>EmailTokens</c> a link can be opened again until it
/// runs out, and a newer one doesn't cancel the older ones, so every email's link keeps working. At most <see cref="MaxLive"/> per
/// ticket: a new one drops the oldest.
/// </summary>
public static class TicketLinks
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    public const int MaxLive = 5;

    /// <summary>A new link for <paramref name="ticket"/>: the ticket with its hash added (and the expired ones dropped), and the link text.</summary>
    public static (Ticket Ticket, string Link) Add(Ticket ticket, DateTimeOffset now)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var live = ticket.Links.Where(l => l.Expires > now).OrderBy(l => l.Expires).ToList();
        while (live.Count >= MaxLive) live.RemoveAt(0);
        live.Add(new TicketLink(Hash(token), now + Lifetime));
        return (ticket with { Links = live }, token);
    }

    /// <summary>Whether <paramref name="token"/> is a live link of <paramref name="ticket"/>. Compared in constant time.</summary>
    public static bool Opens(Ticket ticket, string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return false;
        var hash = System.Text.Encoding.ASCII.GetBytes(Hash(token));
        var found = false;
        foreach (var link in ticket.Links)
            found |= link.Expires > now && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(link.Hash), hash);
        return found;
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
