using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Hypergrid;
using GridGate.Launcher.Core.Settings;
using Xunit;
using static GridGate.Launcher.Tests.SplashPictureApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>The staff's hypergrid links page at /hypergrid (spec 2026-11-05-hypergrid-links-estates-design, 2.9).</summary>
public sealed class HypergridPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private static LauncherFactory On() => Factory(settings: s => s with { Plan = OwnerPlan.SimplePublic, Hostname = "mygrid.example.org", Hypergrid = true });

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url = "/hypergrid")
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string handler, Dictionary<string, string> fields, string tokenFrom = "/hypergrid")
    {
        var page = Parser.ParseDocument(await client.GetStringAsync(tokenFrom, Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await client.PostAsync($"/hypergrid?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static async Task<IHtmlDocument> PostAsync(HttpClient client, string handler, Dictionary<string, string> fields)
    {
        using var r = await PostRawAsync(client, handler, fields);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string? Error(IHtmlDocument page) => page.QuerySelector(".alert-danger")?.TextContent.Trim();
    private static string? Notice(IHtmlDocument page) => page.QuerySelector(".alert-success")?.TextContent.Trim();

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("supervisor")]
    public async Task Every_staff_member_can_list_add_and_remove(string role)
    {
        await using var f = On();
        f.Hyperlinks.AddRegion(1000, 1000);
        var client = await StaffAsync(f, role);

        var added = await PostAsync(client, "Add", new() { ["address"] = "far.example.net:8002:Welcome Island" });
        Assert.Null(Error(added));
        Assert.Contains("Linked Welcome Island on Other Grid at 1000, 1002", Notice(added));
        Assert.Contains("LinkHypergrid 1000 1002 http://far.example.net:8002/ Welcome Island", f.Grid.Snapshot());
        Assert.Equal(new Uri("http://far.example.net:8002/"), Assert.Single(f.Probe.Asked));
        Assert.Contains("hyperlink-added", AuditText(f));

        var id = f.Hyperlinks.Add("Welcome Island", "http://far.example.net:8002/", 1000, 1002);
        var list = await PageAsync(client);
        Assert.Contains("Welcome Island", list.QuerySelector("#hyperlinks")!.TextContent);

        var removed = await PostAsync(client, "Remove", new() { ["link"] = id.ToString() });
        Assert.Contains("Removed the link to Welcome Island", Notice(removed));
        Assert.Empty(f.Hyperlinks.All);
        Assert.Contains("hyperlink-removed", AuditText(f));
    }

    [Fact]
    public async Task Anonymous_and_guests_cannot_open_it()
    {
        await using var f = On();
        Assert.NotEqual(HttpStatusCode.OK, (await Client(f).GetAsync("/hypergrid", Ct)).StatusCode);
        var guest = Client(f);
        await guest.GetAsync("/__test/guest-signin", Ct);
        Assert.NotEqual(HttpStatusCode.OK, (await guest.GetAsync("/hypergrid", Ct)).StatusCode);
    }

    [Fact]
    public async Task Not_there_without_a_guest_side()
    {
        await using var f = Factory(settings: s => s with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, true, 8004, 8006),
        });
        var client = await StaffAsync(f, "owner");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/hypergrid", Ct)).StatusCode);
    }

    [Fact]
    public async Task With_hypergrid_off_links_are_listed_but_none_can_be_added()
    {
        await using var f = Factory(settings: s => s with { Plan = OwnerPlan.SimplePublic, Hostname = "mygrid.example.org", Hypergrid = false });
        f.Hyperlinks.Add("Old Link", "http://far.example.net:8002/", 5, 5);
        var client = await StaffAsync(f, "admin");
        var page = await PageAsync(client);
        Assert.Equal("Hypergrid is off, so links can't be added. Turn it on in Settings, Plan.", page.QuerySelector("#hypergrid-off")!.TextContent.Trim());
        Assert.Null(page.QuerySelector("#hyperlink-add"));
        Assert.Contains("Old Link", page.QuerySelector("#hyperlinks")!.TextContent);

        var posted = await PostAsync(client, "Add", new() { ["address"] = "far.example.net:8002" });
        Assert.Contains("Hypergrid is off", Error(posted));
        Assert.DoesNotContain(f.Grid.Snapshot(), c => c.StartsWith("LinkHypergrid", StringComparison.Ordinal));
        Assert.Empty(f.Probe.Asked);
    }

    [Theory]
    [InlineData("ftp://far.example.net:21/", "Only http")]
    [InlineData("http://far.example.net:8002/admin", "no path")]
    [InlineData("mygrid.example.org:8002", "this grid's own address")]
    public async Task Bad_addresses_are_refused_before_anything_is_asked(string address, string says)
    {
        await using var f = On();
        var page = await PostAsync(await StaffAsync(f, "supervisor"), "Add", new() { ["address"] = address });
        Assert.Contains(says, Error(page));
        Assert.Equal(address, ((IHtmlInputElement)page.QuerySelector("#address")!).Value);
        Assert.Empty(f.Probe.Asked);
        Assert.DoesNotContain(f.Grid.Snapshot(), c => c.StartsWith("LinkHypergrid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_grid_that_fails_the_test_is_not_linked()
    {
        await using var f = On();
        f.Probe.Answer = _ => new ProbeResult(false, null, "far.example.net is on a private network or this computer.");
        var page = await PostAsync(await StaffAsync(f, "admin"), "Add", new() { ["address"] = "far.example.net:8002" });
        Assert.Contains("private network", Error(page));
        Assert.DoesNotContain(f.Grid.Snapshot(), c => c.StartsWith("LinkHypergrid", StringComparison.Ordinal));
        Assert.DoesNotContain("hyperlink-added", AuditText(f));
    }

    [Fact]
    public async Task Robusts_refusal_is_shown()
    {
        await using var f = On();
        f.Grid.OnLinkHypergrid = (_, _, _) => new HyperlinkResult(false, "Remote region could not be found");
        var page = await PostAsync(await StaffAsync(f, "admin"), "Add", new() { ["address"] = "far.example.net:8002:Nowhere" });
        Assert.Contains("Remote region could not be found", Error(page));
        Assert.DoesNotContain("hyperlink-added", AuditText(f));
    }

    [Fact]
    public async Task A_typed_place_is_used_and_a_taken_one_refused()
    {
        await using var f = On();
        f.Hyperlinks.AddRegion(1000, 1000);
        var client = await StaffAsync(f, "admin");

        var taken = await PostAsync(client, "Add", new() { ["address"] = "far.example.net:8002", ["x"] = "1000", ["y"] = "1000" });
        Assert.Contains("already at 1000, 1000", Error(taken));

        var half = await PostAsync(client, "Add", new() { ["address"] = "far.example.net:8002", ["x"] = "1000" });
        Assert.Contains("both X and Y", Error(half));

        var bad = await PostAsync(client, "Add", new() { ["address"] = "far.example.net:8002", ["x"] = "-1", ["y"] = "5" });
        Assert.Contains("whole numbers", Error(bad));

        var ok = await PostAsync(client, "Add", new() { ["address"] = "far.example.net:8002", ["x"] = "2000", ["y"] = "2001" });
        Assert.Null(Error(ok));
        Assert.Contains("LinkHypergrid 2000 2001 http://far.example.net:8002/", f.Grid.Snapshot());
    }

    [Fact]
    public async Task Check_runs_the_test_on_a_link()
    {
        await using var f = On();
        var id = f.Hyperlinks.Add("Far Place", "http://far.example.net:8002/", 5, 5);
        var page = await PostAsync(await StaffAsync(f, "supervisor"), "Check", new() { ["link"] = id.ToString() });
        Assert.Contains("Other Grid answered", Notice(page));
        Assert.Equal(new Uri("http://far.example.net:8002/"), Assert.Single(f.Probe.Asked));
    }

    [Fact]
    public async Task A_database_that_is_down_is_said_in_plain_words()
    {
        await using var f = On();
        f.Hyperlinks.Down = true;
        var page = await PageAsync(await StaffAsync(f, "admin"));
        Assert.NotNull(page.QuerySelector("#hyperlinks-unavailable"));
    }

    [Fact]
    public async Task Removing_a_link_that_is_gone_says_so()
    {
        await using var f = On();
        var page = await PostAsync(await StaffAsync(f, "admin"), "Remove", new() { ["link"] = Guid.NewGuid().ToString() });
        Assert.Contains("isn't there any more", Error(page));
    }

    [Fact]
    public async Task With_hypergrid_off_a_supervisor_is_told_to_ask()
    {
        await using var f = Factory(settings: s => s with { Plan = OwnerPlan.SimplePublic, Hostname = "mygrid.example.org", Hypergrid = false });
        var page = await PageAsync(await StaffAsync(f, "supervisor"));
        Assert.Equal("Hypergrid is off, so links can't be added. Ask the owner or an admin to turn it on.", page.QuerySelector("#hypergrid-off")!.TextContent.Trim());
    }

    [Fact]
    public async Task The_menu_shows_Hypergrid_to_supervisors()
    {
        await using var f = On();
        var page = await PageAsync(await StaffAsync(f, "supervisor"), "/people");
        Assert.NotNull(page.QuerySelector("a.nav-link[href='/hypergrid']"));
        Assert.Null(page.QuerySelector("a.nav-link[href='/estates']"));
    }
}
