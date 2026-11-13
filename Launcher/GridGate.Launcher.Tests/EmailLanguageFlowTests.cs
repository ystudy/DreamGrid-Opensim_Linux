using System.Net;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Email;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Tickets;
using GridGate.Launcher.Email;
using GridGate.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Emails that go to a known resident from staff or about an existing account use the resident's saved language. The owner's own
/// German wording stands in for a German translation here, so the tests prove which language was picked.
/// </summary>
public sealed class EmailLanguageFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly IPAddress Caller = IPAddress.Parse("192.168.1.62");

    private static void GermanWording(SignUpsTests.Rig rig, params string[] ids)
    {
        var s = rig.F.Store.LoadOrCreate();
        rig.F.Store.Save(s with
        {
            Email = new EmailSettings(new Dictionary<string, IReadOnlyDictionary<string, EmailTemplateText>>
            {
                ["de"] = ids.ToDictionary(id => id, id => new EmailTemplateText("Deutsch " + id, "Hallo {AvatarName} {Name} {Link} {Note} {TicketNumber}", DateTimeOffset.UnixEpoch)),
            }),
        });
    }

    private static void Choose(SignUpsTests.Rig rig, Guid principal, string language) =>
        rig.F.Services.GetRequiredService<ResidentLanguages>().Set(principal, language);

    private static Guid Id(SignUpsTests.Rig rig, string first) => rig.F.Robust.All.Single(a => a.FirstName == first).PrincipalId;

    [Fact]
    public async Task Already_have_account_goes_in_the_account_owners_saved_language()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.AlreadyHaveAccount);
        Choose(rig, Id(rig, "Ann"), "de");

        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), Caller, Ct, "en");

        Assert.Equal("Deutsch already-have-account", Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Already_have_account_without_a_saved_language_goes_in_the_page_language()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.AlreadyHaveAccount);

        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), Caller, Ct, "de");

        Assert.Equal("Deutsch already-have-account", Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Already_have_account_without_a_saved_language_on_an_English_page_is_English()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.AlreadyHaveAccount);

        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), Caller, Ct, "en");

        Assert.Equal("Someone tried to sign up on Avalon", Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Approve_emails_in_the_residents_saved_language()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.AccountApproved);
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "en");
        var bea = Id(rig, "Bea");
        Choose(rig, bea, "de");
        await SignUpsTests.SentAsync(rig, 1);

        var r = await rig.F.Services.GetRequiredService<SignUpApprovals>().ApproveAsync(bea, "Test Owner", StaffRole.Owner, Caller, Ct);

        Assert.Equal(ApprovalResult.Done, r.Result);
        Assert.Equal("Deutsch account-approved", (await SignUpsTests.SentAsync(rig, 2))[1].Email.Subject);
    }

    [Fact]
    public async Task Deny_emails_in_the_residents_saved_language_though_the_account_is_deleted()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.SignUpDenied);
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "en");
        var bea = Id(rig, "Bea");
        Choose(rig, bea, "de");
        await SignUpsTests.SentAsync(rig, 1);

        var r = await rig.F.Services.GetRequiredService<SignUpApprovals>().DenyAsync(bea, "Test Owner", StaffRole.Owner, "No.", Caller, Ct);

        Assert.Equal(ApprovalResult.Done, r.Result);
        Assert.Equal("Deutsch signup-denied", (await SignUpsTests.SentAsync(rig, 2))[1].Email.Subject);
        // The saved language goes with the denied account, after the email was worded.
        Assert.Null(rig.F.Services.GetRequiredService<ResidentLanguages>().Get(bea));
    }

    [Fact]
    public async Task Sign_up_confirmation_says_24_Stunden_in_German()
    {
        await using var rig = SignUpsTests.Factory();

        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "de");

        var sent = Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email;
        Assert.Contains("E-Mail", sent.Subject);
        Assert.Contains("24 Stunden", sent.Text);
        Assert.DoesNotContain("hours", sent.Text);
    }

    private static Ticket AddTicket(SignUpsTests.Rig rig, Guid? account, string? language = null) =>
        rig.F.Services.GetRequiredService<TicketStore>().Create(_ => true, (id, n) => new Ticket(id, n, TicketState.Open, TicketCategories.Technical,
            "Region won't start", new TicketSender(account, "Ann Smith", "ann@example.com"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            [new TicketMessage(DateTimeOffset.UtcNow, null, null, "It doesn't start.")], [], Language: language))!;

    [Fact]
    public async Task Staff_reply_to_a_ticket_of_an_account_goes_in_the_residents_saved_language()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.TicketStaffReply);
        var ann = Id(rig, "Ann");
        Choose(rig, ann, "de");

        rig.F.Services.GetRequiredService<SupportTickets>().TellGuestStaffReplied(AddTicket(rig, ann));

        Assert.Equal("Deutsch ticket-staff-reply", Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Staff_reply_to_a_guest_ticket_goes_in_the_grid_default_language()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.TicketStaffReply);
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s ? s with { Languages = s.Languages with { Default = "de" } } : null!);

        rig.F.Services.GetRequiredService<SupportTickets>().TellGuestStaffReplied(AddTicket(rig, null));

        Assert.Equal("Deutsch ticket-staff-reply", Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Ticket_staff_reply_email_is_in_the_tickets_language()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.TicketStaffReply);

        rig.F.Services.GetRequiredService<SupportTickets>().TellGuestStaffReplied(AddTicket(rig, null, "de"));

        Assert.Equal("Deutsch ticket-staff-reply", Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Ticket_language_beats_the_residents_saved_language()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.TicketStaffReply);
        var ann = Id(rig, "Ann");
        Choose(rig, ann, "fr");

        rig.F.Services.GetRequiredService<SupportTickets>().TellGuestStaffReplied(AddTicket(rig, ann, "de"));

        Assert.Equal("Deutsch ticket-staff-reply", Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Ticket_language_the_grid_no_longer_offers_falls_back_to_the_residents_language_then_the_default()
    {
        await using var rig = SignUpsTests.Factory();
        GermanWording(rig, EmailTemplates.TicketStaffReply);
        var ann = Id(rig, "Ann");
        Choose(rig, ann, "de");
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s ? s with { Languages = s.Languages with { Offered = ["en", "de"] } } : null!);
        var lang = rig.F.Services.GetRequiredService<EmailLanguage>();

        Assert.Equal("de", lang.ForTicket(AddTicket(rig, ann, "ru")));
        Assert.Equal("en", lang.ForTicket(AddTicket(rig, null, "ru")));
        Assert.Equal("en", lang.ForTicket(AddTicket(rig, null)));
        Assert.Equal("de", lang.ForTicket(AddTicket(rig, null, "de")));
    }

    /// <summary>Italian, added by the owner and offered, with only the confirm-email texts and "24 hours" translated.</summary>
    private static void Italian(SignUpsTests.Rig rig)
    {
        rig.F.Services.GetRequiredService<OwnerLanguages>().Save(new OwnerLanguage("it", new Dictionary<string, string>(), new Dictionary<string, string>
        {
            ["confirm-email.Subject"] = "Conferma la tua email su {GridName}",
            ["confirm-email.Body"] = "Ciao {AvatarName}, apri {Link} entro {ExpiresIn}.",
            ["ExpiresIn.24h"] = "24 ore",
        }, DateTimeOffset.UnixEpoch));
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s ? s with { Languages = s.Languages with { Offered = ["en", "de", "it"] } } : null!);
    }

    [Fact]
    public async Task Email_in_an_added_language_uses_its_file()
    {
        await using var rig = SignUpsTests.Factory();
        Italian(rig);

        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct, "it");

        var sent = Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email;
        Assert.Equal("Conferma la tua email su Avalon", sent.Subject);
        Assert.Contains("Ciao Bea Example", sent.Text);
        Assert.Contains("entro 24 ore.", sent.Text);
    }

    [Fact]
    public async Task Email_in_an_added_language_falls_back_to_English_per_key()
    {
        await using var rig = SignUpsTests.Factory();
        Italian(rig);
        Choose(rig, Id(rig, "Ann"), "it");

        // Italian has no already-have-account text: that email is in English, to the account's saved language.
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), Caller, Ct, "en");

        Assert.Equal("Someone tried to sign up on Avalon", Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email.Subject);
    }
}
