using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Tickets;
using GridGate.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Support tickets step 3: the staff notices and the keeping sweep (design 2026-11-05-support-tickets). All names are made up.</summary>
public sealed class TicketSweepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TicketStore Store(SignUpsTests.Rig rig) => rig.F.Services.GetRequiredService<TicketStore>();
    private static TicketNotices Notices(SignUpsTests.Rig rig) => rig.F.Services.GetRequiredService<TicketNotices>();
    private static TicketSweep Sweep(SignUpsTests.Rig rig) => rig.F.Services.GetRequiredService<TicketSweep>();
    private static DateTimeOffset Now(SignUpsTests.Rig rig) => rig.F.Time.GetUtcNow();

    /// <summary>A grid with email, and the three test staff given addresses.</summary>
    private static SignUpsTests.Rig Grid(bool email = true) => SignUpsTests.Factory(email: email, before: f =>
    {
        foreach (var role in new[] { StaffRole.Owner, StaffRole.Admin, StaffRole.Supervisor })
        {
            var a = TestAuth.Account(role);
            f.Robust.Add(a.FirstName, a.LastName, a.UserLevel, "pass-word1", a.PrincipalId, email: $"{a.LastName.ToLowerInvariant()}@staff.example");
        }
    });

    private static Ticket Add(SignUpsTests.Rig rig, string subject = "Region won't start", TicketState state = TicketState.Open,
        DateTimeOffset? at = null, Guid? takenBy = null)
    {
        var when = at ?? Now(rig);
        return Store(rig).Create(_ => true, (id, n) => new Ticket(id, n, state, TicketCategories.Technical, subject,
            new TicketSender(null, "Bea Example", "bea@example.com"), when, state == TicketState.Unconfirmed ? null : when, null,
            [new TicketMessage(when, null, null, "Secret words from the guest.")], [], TakenBy: takenBy, TakenByName: takenBy is null ? null : "Test"))!;
    }

    private static void GuestReplies(SignUpsTests.Rig rig, Ticket t) =>
        Store(rig).Update(t.Id, x => x with { Messages = [.. x.Messages, new TicketMessage(Now(rig), null, null, "More secret words.")] });

    private static List<(string To, string Subject, string Text)> StaffMail(SignUpsTests.Rig rig)
    {
        lock (rig.Transport.Sent)
            return rig.Transport.Sent.Where(m => m.To.EndsWith("@staff.example", StringComparison.Ordinal))
                .Select(m => (m.To, m.Email.Subject, m.Email.Text)).ToList();
    }

    private static async Task<List<(string To, string Subject, string Text)>> MailAsync(SignUpsTests.Rig rig, int atLeast)
    {
        for (var i = 0; i < 100 && StaffMail(rig).Count < atLeast; i++) await Task.Delay(25, Ct);
        await Task.Delay(100, Ct); // and nothing more on its way
        return StaffMail(rig);
    }

    private static void SwitchOn(SignUpsTests.Rig rig, params Guid[] staff)
    {
        foreach (var s in staff) Notices(rig).Set(s, true, Now(rig));
        rig.F.Time.Advance(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task New_tickets_go_only_to_staff_who_switched_on_and_hold_no_message_text()
    {
        await using var rig = Grid();
        SwitchOn(rig, TestAuth.AdminId, TestAuth.SupervisorId);
        var t = Add(rig);

        await Sweep(rig).RunOnceAsync(Ct);

        var mail = await MailAsync(rig, 2);
        Assert.Equal(["admin@staff.example", "supervisor@staff.example"], mail.Select(m => m.To).Order());
        Assert.All(mail, m =>
        {
            Assert.Equal("New support tickets on Avalon: " + t.Number, m.Subject);
            Assert.Contains("Region won't start", m.Text);
            Assert.Contains("/people/tickets", m.Text);
            Assert.DoesNotContain("Secret", m.Text);
            Assert.DoesNotContain("bea@example.com", m.Text);
        });
    }

    [Fact]
    public async Task Off_by_default_and_nothing_from_before_switching_on()
    {
        await using var rig = Grid();
        Add(rig, "Before anyone listened");
        rig.F.Time.Advance(TimeSpan.FromMinutes(1));
        await Sweep(rig).RunOnceAsync(Ct);
        Assert.Empty(await MailAsync(rig, 0));

        SwitchOn(rig, TestAuth.OwnerId);
        await Sweep(rig).RunOnceAsync(Ct);
        Assert.Empty(await MailAsync(rig, 0));
    }

    [Fact]
    public async Task At_most_one_an_hour_and_the_rest_come_together()
    {
        await using var rig = Grid();
        SwitchOn(rig, TestAuth.OwnerId);
        var first = Add(rig);
        await Sweep(rig).RunOnceAsync(Ct);
        Assert.Single(await MailAsync(rig, 1));

        rig.F.Time.Advance(TimeSpan.FromMinutes(10));
        var second = Add(rig, "Lost my land");
        var third = Add(rig, "Can't log in");
        await Sweep(rig).RunOnceAsync(Ct);
        Assert.Single(await MailAsync(rig, 1));

        rig.F.Time.Advance(TimeSpan.FromMinutes(51));
        await Sweep(rig).RunOnceAsync(Ct);
        var mail = await MailAsync(rig, 2);
        Assert.Equal(2, mail.Count);
        Assert.Equal($"New support tickets on Avalon: {second.Number}, {third.Number}", mail[1].Subject);
        Assert.DoesNotContain(first.Number, mail[1].Text);
    }

    [Fact]
    public async Task A_guest_reply_goes_to_who_took_it_or_else_to_everyone_switched_on()
    {
        await using var rig = Grid();
        var taken = Add(rig, "Taken one", takenBy: TestAuth.AdminId);
        var free = Add(rig, "Nobody's");
        rig.F.Time.Advance(TimeSpan.FromSeconds(1));
        SwitchOn(rig, TestAuth.OwnerId, TestAuth.AdminId, TestAuth.SupervisorId);

        GuestReplies(rig, taken);
        GuestReplies(rig, free);
        await Sweep(rig).RunOnceAsync(Ct);

        var mail = await MailAsync(rig, 3);
        Assert.Equal(3, mail.Count);
        var admin = Assert.Single(mail, m => m.To == "admin@staff.example");
        Assert.Equal($"Guests replied on Avalon tickets: {taken.Number}, {free.Number}", admin.Subject);
        Assert.All(mail.Where(m => m.To != "admin@staff.example"), m => Assert.Equal("Guests replied on Avalon tickets: " + free.Number, m.Subject));
        Assert.All(mail, m => Assert.DoesNotContain("secret", m.Text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Someone_no_longer_staff_is_switched_off_and_gets_nothing()
    {
        await using var rig = Grid();
        var former = Guid.NewGuid();
        SwitchOn(rig, former);
        Add(rig);
        await Sweep(rig).RunOnceAsync(Ct);
        Assert.Empty(await MailAsync(rig, 0));
        Assert.False(Notices(rig).IsOn(former));
    }

    [Fact]
    public async Task No_email_set_up_means_no_notices_and_nothing_lost()
    {
        await using var rig = Grid(email: false);
        SwitchOn(rig, TestAuth.OwnerId);
        Add(rig);
        await Sweep(rig).RunOnceAsync(Ct);
        Assert.Empty(await MailAsync(rig, 0));
        Assert.Null(Notices(rig).All()[TestAuth.OwnerId].LastSent);
    }

    [Fact]
    public async Task Unconfirmed_tickets_go_after_a_day()
    {
        await using var rig = Grid();
        var old = Add(rig, state: TicketState.Unconfirmed);
        rig.F.Time.Advance(TimeSpan.FromHours(23));
        var young = Add(rig, state: TicketState.Unconfirmed);
        rig.F.Time.Advance(TimeSpan.FromHours(2));

        await Sweep(rig).RunOnceAsync(Ct);

        Assert.Null(Store(rig).Get(old.Id));
        Assert.NotNull(Store(rig).Get(young.Id));
        Assert.Contains(rig.F.Services.GetRequiredService<AuditLog>().Recent(_ => true, 20),
            e => e.Entry.Action == "ticket-deleted" && e.Entry.Target == old.Number);
    }

    [Fact]
    public async Task Closed_tickets_go_a_year_after_closing_and_linked_ones_wait_for_the_case()
    {
        await using var rig = Grid();
        var takedowns = rig.F.Services.GetRequiredService<TakedownStore>();
        var openCase = takedowns.Open(TakedownStoreTestsNotice, true, n => TakedownStoreTests.Build(n)).Number;
        var closedCase = takedowns.Open(TakedownStoreTestsNotice, true, n => TakedownStoreTests.Build(n)).Number;

        Ticket Closed(string subject, string? linked)
        {
            var t = Add(rig, subject);
            return Store(rig).Update(t.Id, x => x with { State = TicketState.Closed, ClosedAt = Now(rig), TakedownCase = linked })!;
        }
        var plain = Closed("Plain", null);
        var waitsOnOpen = Closed("Linked to an open case", openCase);
        var waitsOnClosed = Closed("Linked to a case closed later", closedCase);
        var recent = Store(rig).Update(Add(rig, "Recently closed").Id, x => x with { State = TicketState.Closed })!; // ClosedAt set below

        // The second case closes 100 days after the tickets did.
        rig.F.Time.Advance(TimeSpan.FromDays(100));
        takedowns.Update(closedCase, c => c with { Open = false, Events = [.. c.Events, new CaseEvent(Now(rig), "Test Owner", TestAuth.OwnerId, "closed", null)] });
        Store(rig).Update(recent.Id, x => x with { ClosedAt = Now(rig) });

        rig.F.Time.Advance(TimeSpan.FromDays(266)); // 366 days after the tickets closed
        await Sweep(rig).RunOnceAsync(Ct);
        Assert.Null(Store(rig).Get(plain.Id));
        Assert.NotNull(Store(rig).Get(waitsOnOpen.Id));
        Assert.NotNull(Store(rig).Get(waitsOnClosed.Id));
        Assert.NotNull(Store(rig).Get(recent.Id));

        rig.F.Time.Advance(TimeSpan.FromDays(101)); // a year and a day after the case closed
        await Sweep(rig).RunOnceAsync(Ct);
        Assert.NotNull(Store(rig).Get(waitsOnOpen.Id)); // its case is still open
        Assert.Null(Store(rig).Get(waitsOnClosed.Id));
        Assert.Null(Store(rig).Get(recent.Id));
    }

    private static readonly TakedownNotice TakedownStoreTestsNotice = new(TakedownReason.Copyright, "Rita Rights", "Rights Ltd", "rita@example.com",
        "R-1", new DateOnly(2026, 11, 3), "The sword is ours.");
}
