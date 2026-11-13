using GridGate.Launcher.Core.Tickets;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The ticket files and private links (design 2026-11-05-support-tickets).</summary>
public sealed class TicketStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-tickets-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Ticket Build(Guid id, string number, string subject = "Help") =>
        new(id, number, TicketState.Open, TicketCategories.Technical, subject, new TicketSender(null, "Bea Example", "bea@example.com"),
            Now, Now, null, [new TicketMessage(Now, null, null, "It doesn't work.")], []);

    [Fact]
    public void Numbers_count_up_and_survive_a_restart()
    {
        var store = new TicketStore(_root);
        Assert.Equal("T-0001", store.Create(_ => true, (id, n) => Build(id, n))!.Number);
        var second = store.Create(_ => true, (id, n) => Build(id, n, "Second"))!;
        Assert.Equal("T-0002", second.Number);

        var again = new TicketStore(_root);
        Assert.Equal("Second", again.Get(second.Id)!.Subject);
        Assert.Equal("T-0003", again.Create(_ => true, (id, n) => Build(id, n))!.Number);
    }

    [Fact]
    public void A_refused_create_writes_nothing()
    {
        var store = new TicketStore(_root);
        Assert.Null(store.Create(_ => false, (id, n) => Build(id, n)));
        Assert.Empty(store.All());
    }

    [Fact]
    public void Update_and_delete()
    {
        var store = new TicketStore(_root);
        var t = store.Create(_ => true, (id, n) => Build(id, n))!;
        Assert.Equal(TicketState.Closed, store.Update(t.Id, x => x with { State = TicketState.Closed })!.State);
        Assert.Null(store.Update(t.Id, _ => null));
        Assert.True(store.Delete(t.Id));
        Assert.Null(new TicketStore(_root).Get(t.Id));
    }

    [Fact]
    public void A_link_opens_its_ticket_until_it_runs_out_and_never_another()
    {
        var a = Build(Guid.NewGuid(), "T-0001");
        var b = Build(Guid.NewGuid(), "T-0002");
        (a, var link) = TicketLinks.Add(a, Now);

        Assert.True(TicketLinks.Opens(a, link, Now));
        Assert.True(TicketLinks.Opens(a, link, Now + TimeSpan.FromDays(29)));
        Assert.False(TicketLinks.Opens(a, link, Now + TimeSpan.FromDays(31)));
        Assert.False(TicketLinks.Opens(b, link, Now));
        Assert.False(TicketLinks.Opens(a, "not-a-link", Now));
        Assert.DoesNotContain(a.Links, l => l.Hash.Contains(link, StringComparison.Ordinal));
    }

    [Fact]
    public void A_newer_link_keeps_the_older_ones_up_to_five()
    {
        var t = Build(Guid.NewGuid(), "T-0001");
        var links = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            (t, var link) = TicketLinks.Add(t, Now + TimeSpan.FromMinutes(i));
            links.Add(link);
        }
        Assert.Equal(TicketLinks.MaxLive, t.Links.Count);
        Assert.False(TicketLinks.Opens(t, links[0], Now + TimeSpan.FromMinutes(10)));
        Assert.All(links.Skip(1), l => Assert.True(TicketLinks.Opens(t, l, Now + TimeSpan.FromMinutes(10))));
    }

    [Fact]
    public void Old_ticket_files_without_Language_still_load()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_root);
        // A ticket file as written before tickets kept their language: no "language" property at all.
        File.WriteAllText(Path.Combine(_root, id + ".json"), $$"""
            {
              "id": "{{id}}",
              "number": "T-0007",
              "state": "Open",
              "category": "technical",
              "subject": "Old ticket",
              "sender": { "account": null, "name": "Bea Example", "email": "bea@example.com" },
              "created": "2026-11-05T12:00:00+00:00",
              "confirmed": "2026-11-05T12:00:00+00:00",
              "closedAt": null,
              "messages": [ { "at": "2026-11-05T12:00:00+00:00", "staff": null, "staffName": null, "text": "Help", "note": false } ],
              "links": [],
              "urgent": false
            }
            """);

        var t = Assert.IsType<Ticket>(new TicketStore(_root).Get(id));

        Assert.Equal("T-0007", t.Number);
        Assert.Null(t.Language);
    }

    [Fact]
    public void A_tickets_language_survives_a_restart()
    {
        var id = Guid.NewGuid();
        new TicketStore(_root).Create(_ => true, (_, n) => Build(id, n) with { Language = "es" });
        Assert.Equal("es", new TicketStore(_root).All().Single().Language);
    }
}
