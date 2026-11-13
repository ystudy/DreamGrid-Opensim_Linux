using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Tickets;
using GridGate.Launcher.Resident;
using Microsoft.AspNetCore.Http;

namespace GridGate.Launcher.Email;

/// <summary>
/// The language an email to a resident goes out in. Always one the grid offers (<see cref="OfferedLanguages"/>),
/// otherwise the grid's default: a saved or asked-for language the owner has since taken off the list is not used. Saved values are
/// never trusted: each goes through <see cref="Languages.Match"/>.
/// </summary>
public sealed class EmailLanguage(OfferedLanguages offeredLanguages, ResidentLanguages saved)
{
    /// <summary>The grid's default language, for an email with no resident and no page behind it.</summary>
    public string Default() => Pick(null);

    /// <summary>The language <paramref name="principal"/> chose, if the grid still offers it, else the default.</summary>
    public string ForResident(Guid principal) => Pick(saved.Get(principal));

    /// <summary>The language <paramref name="principal"/> chose, if the grid still offers it, else <paramref name="fallback"/> (itself checked: the default if it is not offered).</summary>
    public string ForResident(Guid principal, string fallback) => Pick(saved.Get(principal), fallback);

    /// <summary>The language the resident page of this request is in (<see cref="ResidentLanguageFilter.Current"/>), if offered; the default when it is not or no resident page ran.</summary>
    public string ForRequest(HttpContext ctx) => Pick(ResidentLanguageFilter.Picked(ctx));

    /// <summary>
    /// The language an email about <paramref name="ticket"/> to its sender goes out in: the language the ticket was sent in when the grid
    /// still offers it, else the saved language of the account that sent it, else the default.
    /// </summary>
    public string ForTicket(Ticket ticket) =>
        Pick(ticket.Language, ticket.Sender.Account is { } account ? saved.Get(account) : null);

    private string Pick(string? tag, string? fallback = null)
    {
        var offered = offeredLanguages.Current();
        return Languages.Match(tag, offered) ?? Languages.Match(fallback, offered) ?? offeredLanguages.Default();
    }
}
