namespace DreamGrid.Launcher.Core.Email;

/// <summary>
/// One mail account the grid sends through. <c>ProtectedPassword</c> is the password as protected by data protection, never the
/// password itself. <c>Order</c> is its place in the list (1 is tried first). <c>Problem</c> is the plain-words reason the
/// account stopped working (for example a refused password); while it is set the account is skipped. A limit of null means no
/// limit of that kind.
/// </summary>
public sealed record EmailAccount(
    Guid Id,
    string Provider,
    string Host,
    int Port,
    MailSecurity Security,
    string UserName,
    string? ProtectedPassword,
    string FromName,
    string FromAddress,
    string? ReplyTo,
    int? PerDay,
    int? PerHour,
    bool Paused,
    int Order,
    string? Problem,
    DateTimeOffset? ProblemSince);
