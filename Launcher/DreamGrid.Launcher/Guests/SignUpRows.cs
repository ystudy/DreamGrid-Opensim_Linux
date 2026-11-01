using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;

namespace DreamGrid.Launcher.Guests;

/// <summary>What the shared sign-up rows partial draws: the rows, whether to draw Approve and Deny, where the buttons return to, and now.</summary>
public sealed record SignUpRowsView(IReadOnlyList<SignUpEntry> Rows, bool CanApprove, string ReturnUrl, DateTimeOffset Now);

/// <summary>Ordering, searching and the words for the sign-up rows on the dashboard card and the sign-ups list (spec 2026-11-01-sign-up-approval-design §7).</summary>
public static class SignUpRows
{
    public const int CardRows = 10;
    public const int PageSize = 25;
    public const string ListPath = "/people/sign-ups";

    /// <summary>Waiting rows first, oldest first; then the rest, newest change first.</summary>
    public static IReadOnlyList<SignUpEntry> Default(IEnumerable<SignUpEntry> rows) =>
        rows.OrderBy(e => e.Waiting ? 0 : 1).ThenBy(e => e.Waiting ? e.Made.UtcTicks : -e.Changed.UtcTicks).ToList();

    /// <summary>
    /// <paramref name="q"/> keeps names containing it (ignoring case); <paramref name="sort"/> is <c>name</c>, <c>made</c> or <c>state</c>
    /// (anything else: <see cref="Default"/>), <paramref name="dir"/> <c>asc</c> or <c>desc</c>.
    /// </summary>
    public static IReadOnlyList<SignUpEntry> Query(IEnumerable<SignUpEntry> rows, string? q, string? sort, string? dir)
    {
        if (!string.IsNullOrWhiteSpace(q)) rows = rows.Where(e => e.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
        var desc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
        IOrderedEnumerable<SignUpEntry>? ordered = sort?.ToLowerInvariant() switch
        {
            "name" => desc ? rows.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            "made" => desc ? rows.OrderByDescending(e => e.Made) : rows.OrderBy(e => e.Made),
            // Waiting first (the order of the enum), then by name.
            "state" => (desc ? rows.OrderByDescending(e => StateOrder(e.State)) : rows.OrderBy(e => StateOrder(e.State)))
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => null,
        };
        return ordered?.ToList() ?? Default(rows);
    }

    private static int StateOrder(SignUpState s) => s switch
    {
        SignUpState.WaitingForEmail or SignUpState.WaitingForStaff => 0,
        SignUpState.SelfApproved or SignUpState.Approved or SignUpState.LetIn => 1,
        _ => 2,
    };

    /// <summary>The badge words for a row.</summary>
    public static string Badge(SignUpEntry e) => e.State switch
    {
        SignUpState.WaitingForEmail => "Waiting for email",
        SignUpState.WaitingForStaff => "Waiting for approval",
        SignUpState.SelfApproved => "Self-approved (email link)",
        SignUpState.Approved => $"Approved by {e.By}",
        SignUpState.LetIn => "Signed up (no approval needed)",
        SignUpState.Denied => $"Denied by {e.By}",
        _ => "",
    };

    /// <summary>"signed up 20 minutes ago".</summary>
    public static string Ago(DateTimeOffset made, DateTimeOffset now)
    {
        var d = now - made;
        static string N(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
        return "signed up " + (d < TimeSpan.FromMinutes(1) ? "just now"
            : d < TimeSpan.FromHours(1) ? N((int)d.TotalMinutes, "minute") + " ago"
            : d < TimeSpan.FromDays(1) ? N((int)d.TotalHours, "hour") + " ago"
            : N((int)d.TotalDays, "day") + " ago");
    }

    /// <summary>A local return path for the buttons: <c>/</c> or the list, never anywhere else.</summary>
    public static string SafeReturn(string? returnUrl, IUrlHelper url) =>
        returnUrl is "/" or ListPath || (returnUrl is not null && url.IsLocalUrl(returnUrl) && returnUrl.StartsWith(ListPath, StringComparison.Ordinal))
            ? returnUrl! : ListPath;
}
