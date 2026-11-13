namespace GridGate.Launcher.Core.Tickets;

/// <summary>Where a ticket is in its life (design 2026-11-05-support-tickets).</summary>
public enum TicketState
{
    /// <summary>Sent by someone not signed in, waiting for them to confirm their email. Staff don't see it.</summary>
    Unconfirmed,
    Open,
    /// <summary>Staff answered and are waiting for the guest.</summary>
    Waiting,
    Closed,
}

/// <summary>The categories a guest picks from.</summary>
public static class TicketCategories
{
    public const string Accounts = "accounts";
    public const string Land = "land";
    public const string Abuse = "abuse";
    public const string Technical = "technical";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [Accounts, Land, Abuse, Technical, Other];

    public static string Title(string id) => id switch
    {
        Accounts => "Accounts and sign-in",
        Land => "Land and regions",
        Abuse => "Report abuse",
        Technical => "Technical problem",
        _ => "Something else",
    };
}

/// <summary>Who sent a ticket: an account here, or a name and email from someone not signed in.</summary>
public sealed record TicketSender(Guid? Account, string Name, string Email);

/// <param name="Staff">The staff member's account id, or null for the guest.</param>
/// <param name="StaffName">What the guest sees for a staff message: their first name.</param>
/// <param name="Note">A staff-only note, never shown to the guest.</param>
public sealed record TicketMessage(DateTimeOffset At, Guid? Staff, string? StaffName, string Text, bool Note = false);

/// <summary>A private link's SHA-256 (lower-case hex) and when it stops working. The link itself is kept nowhere.</summary>
public sealed record TicketLink(string Hash, DateTimeOffset Expires);

public sealed record Ticket(
    Guid Id,
    string Number,
    TicketState State,
    string Category,
    string Subject,
    TicketSender Sender,
    DateTimeOffset Created,
    DateTimeOffset? Confirmed,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<TicketMessage> Messages,
    IReadOnlyList<TicketLink> Links,
    string? Evidence = null,
    string? Logs = null,
    Guid? TakenBy = null,
    string? TakenByName = null,
    bool Urgent = false,
    string? TakedownCase = null,
    string? Language = null)
{
    /// <summary>Open or waiting: what counts against a sender's open tickets.</summary>
    public bool IsActive => State is TicketState.Open or TicketState.Waiting;

    /// <summary>The time of the last message, or when it was made.</summary>
    public DateTimeOffset LastActivity => Messages.Count > 0 ? Messages[^1].At : Created;
}
