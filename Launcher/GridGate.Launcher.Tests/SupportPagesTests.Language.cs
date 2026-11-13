using GridGate.Launcher.Core.Email;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Tickets;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>A ticket keeps the language of the page it was sent from, and its emails go out in it.</summary>
public sealed partial class SupportPagesTests
{
    private static void GermanWording(Rig r) => r.F.Store.Save(r.F.Store.LoadOrCreate() with
    {
        Email = new EmailSettings(new Dictionary<string, IReadOnlyDictionary<string, EmailTemplateText>>
        {
            ["de"] = new[] { EmailTemplates.TicketConfirm, EmailTemplates.TicketAccess }.ToDictionary(id => id,
                id => new EmailTemplateText("Deutsch " + id, "Hallo {Name} {Link} {TicketNumber}", DateTimeOffset.UnixEpoch)),
        }),
    });

    [Fact]
    public async Task Ticket_remembers_the_page_language()
    {
        await using var r = Make();
        var guest = Client(r);
        guest.DefaultRequestHeaders.Add("Cookie", "dg-lang=de");

        using (await PostAsync(guest, "/support", Form())) { }

        Assert.Equal("de", Assert.Single(r.Store.All()).Language);
    }

    [Fact]
    public async Task A_ticket_from_an_English_page_remembers_English()
    {
        await using var r = Make();

        using (await PostAsync(Client(r), "/support", Form())) { }

        Assert.Equal("en", Assert.Single(r.Store.All()).Language);
    }

    [Fact]
    public async Task The_confirm_and_access_emails_use_the_tickets_language_not_the_clicking_browsers()
    {
        await using var r = Make();
        GermanWording(r);
        var guest = Client(r);
        guest.DefaultRequestHeaders.Add("Cookie", "dg-lang=de");
        using (await PostAsync(guest, "/support", Form())) { }
        var confirm = await EmailLinkAsync(r, "/support/confirm");

        // Opened in a browser with no language cookie: the email after it is still the ticket's.
        using (await PostAsync(Client(r), confirm, new() { ["Token"] = confirm.Split("token=")[1] })) { }
        await EmailLinkAsync(r, "/support/ticket", 1);

        Assert.Equal("Deutsch " + EmailTemplates.TicketConfirm, r.Transport.Sent[0].Email.Subject);
        Assert.Equal("Deutsch " + EmailTemplates.TicketAccess, r.Transport.Sent[1].Email.Subject);
    }
}
