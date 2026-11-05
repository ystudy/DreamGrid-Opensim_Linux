using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The inventory, takedown, case and sender pages on the real host (spec 2026-11-04-inventory-takedown-design).</summary>
public sealed class TakedownPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid AnnId = Guid.Parse("2a000000-0000-0000-0000-00000000002a");

    private sealed record Rig(LauncherFactory F, Guid Root, Guid Trash) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => F.DisposeAsync();
        public TakedownActions Actions => F.Services.GetRequiredService<TakedownActions>();
    }

    private static Rig Factory()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(new EmailPageTests.FakeTransport()),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Holder", 0, "pass-word1", AnnId, email: "ann@example.com");
        var root = f.Inventory.AddDefaultFolders(AnnId);
        var trash = f.Inventory.FoldersAsync(AnnId, CancellationToken.None).GetAwaiter().GetResult().Single(x => x.Type == 14).Id;
        return new Rig(f, root, trash);
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, IEnumerable<KeyValuePair<string, string>> fields, string tokenFrom)
    {
        var page = await PageAsync(client, tokenFrom);
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await client.PostAsync(url, new FormUrlEncodedContent([.. fields, new("__RequestVerificationToken", token)]), Ct);
    }

    private static List<JsonElement> Audit(LauncherFactory f, string action)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("action").GetString() == action).ToList();
    }

    private static async Task<string> OpenCaseAsync(Rig rig, string itemName = "Sword", string? creator = null)
    {
        var item = rig.F.Inventory.Add(AnnId, rig.Root, itemName, null, 6, creator);
        var r = await rig.Actions.TakeDownAsync(TakedownActionsTests.As(StaffRole.Admin), null, AnnId, [item.ItemId], null,
            TakedownActionsTests.Notice(), false, Ct);
        Assert.True(r.Done, r.Message);
        return r.Number!;
    }

    [Fact]
    public async Task Inventory_page_lists_folders_and_items_and_audits_the_view()
    {
        await using var rig = Factory();
        rig.F.Inventory.Add(AnnId, rig.Root, "Red hat");
        var admin = await SignedInAsync(rig.F, "admin");

        var page = await PageAsync(admin, $"/people/{AnnId}/inventory");

        Assert.Contains("Trash", page.QuerySelector("#inventory-folders")!.TextContent);
        var row = Assert.Single(page.QuerySelectorAll("#inventory-items tbody tr"));
        Assert.Contains("Red hat", row.TextContent);
        Assert.NotNull(row.QuerySelector("input[type=checkbox][name=items]"));
        Assert.Equal("Ann Holder", Assert.Single(Audit(rig.F, "inventory-viewed")).GetProperty("target").GetString());
    }

    [Fact]
    public async Task Inventory_search_finds_items_in_trash()
    {
        await using var rig = Factory();
        rig.F.Inventory.Add(AnnId, rig.Trash, "Thrown away sword");
        rig.F.Inventory.Add(AnnId, rig.Root, "Hat");
        var admin = await SignedInAsync(rig.F, "admin");

        var page = await PageAsync(admin, $"/people/{AnnId}/inventory?q=sword");

        var row = Assert.Single(page.QuerySelectorAll("#inventory-items tbody tr"));
        Assert.Contains("Thrown away sword", row.TextContent);
        Assert.Contains("Trash", row.TextContent);
    }

    [Fact]
    public async Task Supervisor_gets_403_on_every_takedown_page()
    {
        await using var rig = Factory();
        var number = await OpenCaseAsync(rig);
        var sup = await SignedInAsync(rig.F, "supervisor");
        foreach (var url in new[] { $"/people/{AnnId}/inventory", $"/people/{AnnId}/takedown", "/people/takedowns", $"/people/takedowns/{number}",
                     $"/people/takedowns/{number}/sender" })
        {
            using var r = await sup.GetAsync(url, Ct);
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden, $"{url}: {r.StatusCode}");
        }
    }

    [Fact]
    public async Task Take_down_form_posts_and_redirects_to_the_case()
    {
        await using var rig = Factory();
        var sword = rig.F.Inventory.Add(AnnId, rig.Root, "Sword");
        var admin = await SignedInAsync(rig.F, "admin");
        var form = $"/people/{AnnId}/takedown?items={sword.ItemId}";
        var shown = await PageAsync(admin, form);
        Assert.Contains("Sword", shown.QuerySelector("#takedown-items")!.TextContent);

        using var r = await PostAsync(admin, form, [
            new("items", sword.ItemId.ToString()), new("Reason", "Copyright"), new("SenderName", "Rita Rights"), new("Received", "2026-11-03"),
            new("Text", "The sword is ours."), new("Tell", "true")], form);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Matches(@"^/people/takedowns/TD-\d{4}-001$", r.Headers.Location!.OriginalString);
        Assert.DoesNotContain(rig.F.Inventory.Items, i => i.ItemId == sword.ItemId);
    }

    [Fact]
    public async Task Case_page_shows_put_back_and_find_copies_while_open_and_no_forms_when_closed()
    {
        await using var rig = Factory();
        var number = await OpenCaseAsync(rig);
        var admin = await SignedInAsync(rig.F, "admin");

        var open = await PageAsync(admin, $"/people/takedowns/{number}");
        Assert.NotNull(open.QuerySelector("form#find-copies"));
        Assert.NotNull(open.QuerySelector("#case-items form.put-back"));
        Assert.Contains("Ann Holder", open.QuerySelector("#case-people")!.TextContent);
        Assert.Contains("aren't covered", open.QuerySelector("#case-regions-note")!.TextContent);

        rig.Actions.Close(TakedownActionsTests.As(StaffRole.Admin), null, number, "removed for good");
        var closed = await PageAsync(admin, $"/people/takedowns/{number}");
        Assert.Empty(closed.QuerySelectorAll("main form"));
        Assert.Contains("removed for good", closed.QuerySelector("#case-outcome")!.TextContent);
    }

    [Fact]
    public async Task Sender_page_has_no_holder_name()
    {
        await using var rig = Factory();
        var number = await OpenCaseAsync(rig, "Ann's own upload", AnnId.ToString());
        var admin = await SignedInAsync(rig.F, "admin");

        var page = await PageAsync(admin, $"/people/takedowns/{number}/sender");
        var text = page.QuerySelector("#sender-record")!.TextContent;

        Assert.DoesNotContain("Ann Holder", text);
        Assert.DoesNotContain("My Inventory", text);
        Assert.Contains("a resident", text);
        Assert.Contains("Rita Rights", text);
        Assert.Contains("not covered by this list", text);
    }

    [Fact]
    public async Task Person_page_shows_the_hold_and_counts_to_admins_only()
    {
        await using var rig = Factory();
        var number = await OpenCaseAsync(rig);
        var admin = await PageAsync(await SignedInAsync(rig.F, "admin"), $"/people/{AnnId}");
        Assert.Contains("named in 1 (1 open); held a copy in 0", admin.QuerySelector("#person-takedowns")!.TextContent);
        Assert.Contains(number, admin.QuerySelector("#person-hold")!.TextContent);

        var sup = await PageAsync(await SignedInAsync(rig.F, "supervisor"), $"/people/{AnnId}");
        Assert.Null(sup.QuerySelector("#person-takedowns"));
        Assert.Null(sup.QuerySelector("#person-hold"));
    }

    [Fact]
    public async Task Supervisor_history_hides_takedown_lines()
    {
        await using var rig = Factory();
        await OpenCaseAsync(rig);
        var admin = await PageAsync(await SignedInAsync(rig.F, "admin"), $"/people/{AnnId}");
        Assert.Contains("takedown-removed", admin.QuerySelector("#person-history")!.TextContent);

        var sup = await PageAsync(await SignedInAsync(rig.F, "supervisor"), $"/people/{AnnId}");
        Assert.DoesNotContain("takedown-", sup.QuerySelector("#person-history")!.TextContent);
        Assert.DoesNotContain("inventory-viewed", sup.QuerySelector("#person-history")!.TextContent);
    }

    [Fact]
    public async Task Inventory_icon_is_disabled_for_staff_with_the_reason()
    {
        await using var rig = Factory();
        var admin = await SignedInAsync(rig.F, "owner");

        var person = await PageAsync(admin, $"/people/{AnnId}");
        Assert.Equal($"/people/{AnnId}/inventory", person.QuerySelector("[data-task=Inventory]")!.GetAttribute("href"));

        var staff = await PageAsync(admin, $"/people/{TestAuth.AdminId}");
        var icon = staff.QuerySelector("[data-task=Inventory]")!;
        Assert.True(icon.HasAttribute("disabled"));
        Assert.Equal(PeopleRules.StaffFirst, icon.GetAttribute("title"));
    }
}
