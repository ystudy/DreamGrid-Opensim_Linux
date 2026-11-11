using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Tickets;
using DreamGrid.Launcher.Email;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The guest side of support tickets (design 2026-11-05-support-tickets, step 1).</summary>
public sealed partial class SupportPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private sealed record Rig(LauncherFactory F, EmailPageTests.FakeTransport Transport) : IAsyncDisposable
    {
        public TicketStore Store => F.Services.GetRequiredService<TicketStore>();
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    private static Rig Make(bool email = true, OwnerPlan plan = OwnerPlan.Local, IPAddress? caller = null, string[]? adminNetworks = null)
    {
        var transport = new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = plan,
            Join = plan == OwnerPlan.RegionServer
                ? new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006)
                : s.Join,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Bots = new BotSettings(Honeypot: true, RateLimit: false, ProofOfWork: false),
            Security = adminNetworks is null ? s.Security : s.Security with { AdminNetworks = adminNetworks },
        })
        {
            Before = app =>
            {
                if (caller is not null)
                    app.Use((ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = caller;
                        return next(ctx);
                    });
                TestAuth.Apply(app);
            },
            ExtraServices = services => services.AddSingleton<IMailTransport>(transport),
        };
        TestAuth.AddStaff(f.Robust);
        if (email)
        {
            var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
                "grid@gmail.com", null, 500, null, false, 0, null, null);
            f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
        }
        return new Rig(f, transport);
    }

    private static HttpClient Client(Rig r) => r.F.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields)
    {
        var page = await PageAsync(client, url);
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static Dictionary<string, string> Form(string message = "My region won't start since yesterday.") => new()
    {
        ["Name"] = "Bea Example", ["Email"] = "bea@example.com", ["Category"] = TicketCategories.Technical,
        ["Subject"] = "Region won't start", ["Message"] = message,
    };

    private static async Task<string> EmailLinkAsync(Rig r, string path, int index = 0)
    {
        for (var i = 0; i < 100 && r.Transport.Sent.Count <= index; i++) await Task.Delay(50, Ct);
        var text = r.Transport.Sent[index].Email.Text;
        var m = Regex.Match(text, "http://[^\\s]+" + Regex.Escape(path) + "[^\\s]+");
        Assert.True(m.Success, text);
        return new Uri(m.Value).PathAndQuery;
    }

    [Fact]
    public async Task Someone_not_signed_in_confirms_by_email_then_opens_and_replies()
    {
        await using var r = Make();
        var guest = Client(r);

        using (var sent = await PostAsync(guest, "/support", Form()))
            Assert.Contains("Check your email", Parser.ParseDocument(await sent.Content.ReadAsStringAsync(Ct)).QuerySelector("#support-reply")!.TextContent);
        var ticket = Assert.Single(r.Store.All());
        Assert.Equal(TicketState.Unconfirmed, ticket.State);

        // The link shows a button; only pressing it sends the ticket.
        var confirm = await EmailLinkAsync(r, "/support/confirm");
        Assert.Equal(TicketState.Unconfirmed, r.Store.Get(ticket.Id)!.State);
        using (var pressed = await PostAsync(guest, confirm, new() { ["Token"] = confirm.Split("token=")[1] }))
            Assert.Equal(HttpStatusCode.Redirect, pressed.StatusCode);
        Assert.Equal(TicketState.Open, r.Store.Get(ticket.Id)!.State);

        // A second press of the same link does nothing.
        using (var again = await PostAsync(Client(r), confirm, new() { ["Token"] = confirm.Split("token=")[1] }))
            // Read as text: Razor encodes the apostrophe in the page's own words.
            Assert.Contains("doesn't work any more", Parser.ParseDocument(await again.Content.ReadAsStringAsync(Ct)).Body!.TextContent);

        // The private link from the second email opens it in another browser, and the reply goes on the ticket.
        var link = await EmailLinkAsync(r, "/support/ticket", 1);
        var other = Client(r);
        using (var opened = await other.GetAsync(link, Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, opened.StatusCode);
            Assert.DoesNotContain("link=", opened.Headers.Location!.OriginalString);
        }
        var page = await PageAsync(other, "/support/ticket?id=" + ticket.Id);
        Assert.Equal("Region won't start", page.QuerySelector("h1")!.TextContent);
        using (await PostAsync(other, "/support/ticket?id=" + ticket.Id, new() { ["Id"] = ticket.Id.ToString(), ["Reply"] = "Still broken this morning." })) { }
        Assert.Equal(2, r.Store.Get(ticket.Id)!.Messages.Count);
    }

    [Fact]
    public async Task A_link_or_cookie_for_one_ticket_never_opens_another()
    {
        await using var r = Make();
        var a = r.Store.Create(_ => true, (id, n) => Ticket(id, n, "Ticket A"))!;
        var b = r.Store.Create(_ => true, (id, n) => Ticket(id, n, "Ticket B"))!;
        var (withLink, link) = TicketLinks.Add(a, DateTimeOffset.UtcNow);
        r.Store.Update(a.Id, _ => withLink);
        var client = Client(r);

        using (await client.GetAsync($"/support/ticket?id={a.Id}&link={link}", Ct)) { }
        Assert.Equal("Ticket A", (await PageAsync(client, "/support/ticket?id=" + a.Id)).QuerySelector("h1")!.TextContent);
        Assert.Contains("run out", (await PageAsync(client, "/support/ticket?id=" + b.Id)).QuerySelector("h1")!.TextContent);
        Assert.Contains("run out", (await PageAsync(Client(r), $"/support/ticket?id={b.Id}&link={link}")).QuerySelector("h1")!.TextContent);
    }

    [Fact]
    public async Task Messages_show_as_text_and_staff_notes_never_reach_the_guest()
    {
        await using var r = Make();
        var t = r.Store.Create(_ => true, (id, n) => Ticket(id, n, "Hi") with
        {
            Messages =
            [
                new TicketMessage(DateTimeOffset.UtcNow, null, null, "<script>alert(1)</script>"),
                new TicketMessage(DateTimeOffset.UtcNow, TestAuth.OwnerId, "Test", "Secret staff note", Note: true),
            ],
            Evidence = "javascript:alert(2)",
        })!;
        var (withLink, link) = TicketLinks.Add(t, DateTimeOffset.UtcNow);
        r.Store.Update(t.Id, _ => withLink);
        var client = Client(r);
        using (await client.GetAsync($"/support/ticket?id={t.Id}&link={link}", Ct)) { }
        var html = await client.GetStringAsync("/support/ticket?id=" + t.Id, Ct);

        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("&lt;script&gt;alert(1)", html);
        Assert.DoesNotContain("Secret staff note", html);
        Assert.DoesNotContain("href=\"javascript:", html);
    }

    [Fact]
    public async Task A_signed_in_resident_sends_at_once_and_sees_it_listed()
    {
        await using var r = Make(email: false);
        var guest = Client(r);
        (await guest.GetAsync("/__test/guest-signin", Ct)).Dispose();

        using (var sent = await PostAsync(guest, "/support", Form()))
            Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        var t = Assert.Single(r.Store.All());
        Assert.Equal(TicketState.Open, t.State);
        Assert.Equal(TestAuth.GuestAccount.PrincipalId, t.Sender.Account);
        Assert.NotNull((await PageAsync(guest, "/support")).QuerySelector($"#my-tickets a[href='/support/ticket?id={t.Id}']"));
        Assert.Equal("Region won't start", (await PageAsync(guest, "/support/ticket?id=" + t.Id)).QuerySelector("h1")!.TextContent);
    }

    [Fact]
    public async Task Without_email_someone_not_signed_in_is_asked_to_sign_in()
    {
        await using var r = Make(email: false);
        var page = await PageAsync(Client(r), "/support");
        Assert.NotNull(page.QuerySelector("#support-sign-in"));
        Assert.Null(page.QuerySelector("#support-form"));
    }

    [Fact]
    public async Task A_fourth_open_ticket_from_one_address_is_refused()
    {
        await using var r = Make();
        for (var i = 0; i < 3; i++) r.Store.Create(_ => true, (id, n) => Ticket(id, n, "Old " + i));
        // Three of Bea's tickets are open. Someone not signed in gets the usual "check your email", which says nothing about the
        // address, and nothing is kept or sent.
        using (var fourth = await PostAsync(Client(r), "/support", Form()))
            Assert.NotNull(Parser.ParseDocument(await fourth.Content.ReadAsStringAsync(Ct)).QuerySelector("#support-reply"));
        Assert.Equal(3, r.Store.All().Count);
        Assert.Empty(r.Transport.Sent);
    }

    [Theory]
    [InlineData("Victim <victim@example.com>")]
    [InlineData("victim@example.com, other@example.com")]
    [InlineData("not-an-address")]
    public async Task An_email_written_any_other_way_than_one_plain_address_is_refused(string typed)
    {
        await using var r = Make();
        using var bad = await PostAsync(Client(r), "/support", new Dictionary<string, string>(Form()) { ["Email"] = typed });
        Assert.Contains("Write the email address we should answer.", await bad.Content.ReadAsStringAsync(Ct));
        Assert.Empty(r.Store.All());
    }

    [Fact]
    public async Task Only_three_confirmation_emails_a_day_go_to_one_address_and_nothing_more_is_kept()
    {
        await using var r = Make();
        for (var i = 0; i < 5; i++)
            using (await PostAsync(Client(r), "/support", new Dictionary<string, string>(Form()) { ["Email"] = i % 2 == 0 ? "Bea@Example.com" : "bea@example.com" })) { }
        Assert.Equal(3, r.Store.All().Count);
    }

    [Fact]
    public async Task The_form_checks_its_fields()
    {
        await using var r = Make();
        using var bad = await PostAsync(Client(r), "/support", Form(message: "short"));
        Assert.Contains("Write at least 10 characters", await bad.Content.ReadAsStringAsync(Ct));
        Assert.Empty(r.Store.All());
    }

    [Fact]
    public async Task Lost_link_gives_the_same_reply_for_any_address()
    {
        await using var r = Make();
        var known = await PostAsync(Client(r), "/support/lost-link", new() { ["Email"] = "bea@example.com" });
        var unknown = await PostAsync(Client(r), "/support/lost-link", new() { ["Email"] = "nobody@example.com" });
        Assert.Equal(
            Parser.ParseDocument(await known.Content.ReadAsStringAsync(Ct)).QuerySelector("#lost-link-reply")!.TextContent,
            Parser.ParseDocument(await unknown.Content.ReadAsStringAsync(Ct)).QuerySelector("#lost-link-reply")!.TextContent);
    }

    [Theory]
    [InlineData("/support")]
    [InlineData("/support/lost-link")]
    [InlineData("/support/confirm?token=x")]
    public async Task There_are_no_support_pages_on_a_region_server(string url)
    {
        await using var r = Make(plan: OwnerPlan.RegionServer);
        using var resp = await Client(r).GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Residents_reach_support_from_outside_the_admin_networks()
    {
        await using var r = Make(caller: IPAddress.Parse("192.168.1.50"), adminNetworks: ["10.0.0.0/8"]);
        var guest = Client(r);
        using (var staff = await guest.GetAsync("/login", Ct)) Assert.Equal(HttpStatusCode.NotFound, staff.StatusCode); // the list is on

        using (await PostAsync(guest, "/support", Form())) { }
        var ticket = Assert.Single(r.Store.All());
        var confirm = await EmailLinkAsync(r, "/support/confirm");
        using (var pressed = await PostAsync(guest, confirm, new() { ["Token"] = confirm.Split("token=")[1] }))
            Assert.Equal(HttpStatusCode.Redirect, pressed.StatusCode);
        Assert.Equal(TicketState.Open, r.Store.Get(ticket.Id)!.State);

        var link = await EmailLinkAsync(r, "/support/ticket", 1);
        var other = Client(r);
        using (var opened = await other.GetAsync(link, Ct)) Assert.Equal(HttpStatusCode.Redirect, opened.StatusCode);
        Assert.Equal("Region won't start", (await PageAsync(other, "/support/ticket?id=" + ticket.Id)).QuerySelector("h1")!.TextContent);
    }

    [Fact]
    public async Task The_guest_header_links_to_help()
    {
        await using var r = Make();
        Assert.Equal("/support", (await PageAsync(Client(r), "/support")).QuerySelector("header #help-link")!.GetAttribute("href"));
    }

    private static Ticket Ticket(Guid id, string number, string subject) =>
        new(id, number, TicketState.Open, TicketCategories.Technical, subject, new TicketSender(null, "Bea Example", "bea@example.com"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, [new TicketMessage(DateTimeOffset.UtcNow, null, null, "Help me please.")], []);
}
