using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Tickets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static GridGate.Launcher.Tests.SplashPictureApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>The staff side of support tickets (design 2026-11-05-support-tickets, step 2).</summary>
public sealed class StaffTicketsPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private static TicketStore Store(LauncherFactory f) => f.Services.GetRequiredService<TicketStore>();

    private static Ticket Add(LauncherFactory f, string subject = "Region won't start", string category = TicketCategories.Technical,
        TicketState state = TicketState.Open) =>
        Store(f).Create(_ => true, (id, n) => new Ticket(id, n, state, category, subject,
            new TicketSender(null, "Bea Example", "bea@example.com"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            [new TicketMessage(DateTimeOffset.UtcNow, null, null, "It doesn't start since yesterday.")], []))!;

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string number, string handler, Dictionary<string, string>? fields = null)
    {
        var page = await PageAsync(client, "/people/tickets/" + number);
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        return await client.PostAsync($"/people/tickets/{number}?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>(fields ?? []) { ["__RequestVerificationToken"] = token }), Ct);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("supervisor")]
    public async Task Every_staff_member_sees_and_answers_tickets(string role)
    {
        await using var f = Factory();
        var t = Add(f);
        Add(f, "Unconfirmed one", state: TicketState.Unconfirmed);
        var staff = await StaffAsync(f, role);

        var list = await PageAsync(staff, "/people/tickets");
        Assert.Equal([t.Number], list.QuerySelectorAll("#tickets tbody tr td:first-child a").Select(a => a.TextContent.Trim()));

        using (var r = await PostAsync(staff, t.Number, "Reply", new() { ["Text"] = "Try restarting it from Regions." }))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var saved = Store(f).Get(t.Id)!;
        Assert.Equal(TicketState.Waiting, saved.State);
        Assert.Equal("Try restarting it from Regions.", saved.Messages[^1].Text);
        Assert.Equal("Test", saved.Messages[^1].StaffName); // the first name only
        Assert.NotNull(saved.TakenBy); // answering takes it
    }

    [Fact]
    public async Task A_note_stays_with_staff_and_actions_are_audited_without_the_text()
    {
        await using var f = Factory();
        var t = Add(f);
        var staff = await StaffAsync(f, "admin");

        using (await PostAsync(staff, t.Number, "Note", new() { ["Text"] = "Secret staff thought." })) { }
        using (await PostAsync(staff, t.Number, "Urgent")) { }
        using (await PostAsync(staff, t.Number, "Close")) { }

        var saved = Store(f).Get(t.Id)!;
        Assert.True(saved.Messages[^1].Note);
        Assert.True(saved.Urgent);
        Assert.Equal(TicketState.Closed, saved.State);
        var audit = f.Services.GetRequiredService<AuditLog>().Recent(_ => true, 50).Select(e => e.Entry).ToList();
        Assert.Contains(audit, e => e.Action == "ticket-note" && e.Target == t.Number);
        Assert.Contains(audit, e => e.Action == "ticket-close");
        Assert.DoesNotContain(audit, e => (e.Target ?? "").Contains("Secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Take_never_quietly_takes_a_colleagues_ticket()
    {
        await using var f = Factory();
        var t = Add(f);
        var admin = await StaffAsync(f, "admin");
        var supervisor = await StaffAsync(f, "supervisor");

        using (await PostAsync(admin, t.Number, "Take")) { }
        var held = Store(f).Get(t.Id)!.TakenBy;
        Assert.NotNull(held);

        using (await PostAsync(supervisor, t.Number, "Take")) { }
        Assert.Equal(held, Store(f).Get(t.Id)!.TakenBy); // refused: the admin has it
        using (await PostAsync(supervisor, t.Number, "LetGo")) { }
        Assert.Equal(held, Store(f).Get(t.Id)!.TakenBy); // a supervisor can't let someone else's go

        using (await PostAsync(supervisor, t.Number, "TakeOver")) { }
        Assert.NotEqual(held, Store(f).Get(t.Id)!.TakenBy); // on purpose, it can
    }

    [Fact]
    public async Task Evidence_is_text_never_a_link()
    {
        await using var f = Factory();
        var t = Add(f);
        Store(f).Update(t.Id, x => x with { Evidence = "https://evil.example/x.png" });
        var page = await PageAsync(await StaffAsync(f, "admin"), "/people/tickets/" + t.Number);
        Assert.Equal("https://evil.example/x.png", page.QuerySelector("#evidence")!.TextContent);
        Assert.Null(page.QuerySelector("a[href^='https://evil']"));
        Assert.Null(page.QuerySelector("img[src^='https://evil']"));
    }

    [Fact]
    public async Task Supervisors_neither_see_nor_set_the_takedown_link()
    {
        await using var f = Factory();
        var t = Add(f, category: TicketCategories.Abuse);
        var supervisor = await StaffAsync(f, "supervisor");
        Assert.Null((await PageAsync(supervisor, "/people/tickets/" + t.Number)).QuerySelector("#takedown"));

        using (await PostAsync(supervisor, t.Number, "Link", new() { ["CaseNumber"] = "TD-2026-001" })) { }
        Assert.Null(Store(f).Get(t.Id)!.TakedownCase);

        var owner = await StaffAsync(f, "owner");
        Assert.NotNull((await PageAsync(owner, "/people/tickets/" + t.Number)).QuerySelector("#takedown"));
        using (await PostAsync(owner, t.Number, "Link", new() { ["CaseNumber"] = "TD-2099-999" })) { }
        Assert.Null(Store(f).Get(t.Id)!.TakedownCase); // no such case
    }

    [Fact]
    public async Task Unconfirmed_tickets_and_unknown_numbers_are_not_found()
    {
        await using var f = Factory();
        var hidden = Add(f, state: TicketState.Unconfirmed);
        var staff = await StaffAsync(f, "admin");
        using var r1 = await staff.GetAsync("/people/tickets/" + hidden.Number, Ct);
        using var r2 = await staff.GetAsync("/people/tickets/T-9999", Ct);
        Assert.Equal(HttpStatusCode.NotFound, r1.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, r2.StatusCode);
    }

    [Fact]
    public async Task Guests_and_strangers_cannot_open_the_staff_pages()
    {
        await using var f = Factory();
        var t = Add(f);
        using var r = await Client(f).GetAsync("/people/tickets/" + t.Number, Ct);
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task There_are_no_ticket_pages_on_a_region_server()
    {
        await using var f = Factory(settings: s => s with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006),
            Hypergrid = false,
        });
        using var r = await (await StaffAsync(f, "owner")).GetAsync("/people/tickets", Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
