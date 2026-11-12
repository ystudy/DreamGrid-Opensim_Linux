using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The "Inventory backups" panel on a person's page: their two switches and own limits (IAR plan task 2).</summary>
public sealed class PersonIarTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid AnnId = Guid.Parse("0c000000-0000-0000-0000-00000000000c");

    private static LauncherFactory Factory()
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
        f.Robust.Add("Ann", "Lee", 0, "pass-word1", AnnId, email: "ann@example.com");
        return f;
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

    /// <summary>Posts the panel: a switch is on when its name is in <paramref name="on"/>; an override left out uses the grid's.</summary>
    private static async Task<IHtmlDocument> PostIarAsync(HttpClient client, string[] on, Dictionary<string, string>? limits = null)
    {
        var url = $"/people/{AnnId}";
        var token = (await PageAsync(client, url)).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(limits ?? []) { ["id"] = AnnId.ToString(), ["__RequestVerificationToken"] = token };
        foreach (var name in on) form[name] = "true";
        using var r = await client.PostAsync(url + "?handler=Iar", new FormUrlEncodedContent(form), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static List<AuditEntry> Audit(LauncherFactory f) =>
        [.. f.Services.GetRequiredService<AuditLog>().Recent(e => e.Entry.Action == "iar-rights-changed", 50).Select(e => e.Entry)];

    [Fact]
    public async Task Panel_shows_switches_and_grid_values()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        var page = await PageAsync(admin, $"/people/{AnnId}");
        var panel = page.QuerySelector("#person-iar")!;
        Assert.True(panel.QuerySelector("input#IarDownloadOn")!.HasAttribute("checked"));
        Assert.True(panel.QuerySelector("input#IarUploadOn")!.HasAttribute("checked"));
        Assert.Equal("", panel.QuerySelector("input#IarUploadsPerDay")!.GetAttribute("value") ?? "");
        Assert.Contains("Use the grid's (2)", panel.TextContent);
    }

    [Fact]
    public async Task Owner_and_admin_can_set_and_it_is_audited()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");

        var page = await PostIarAsync(admin, ["IarUploadOn"], new() { ["IarUploadsPerDay"] = "10" });

        var r = f.Services.GetRequiredService<IarRights>().For(AnnId);
        Assert.True(r.DownloadOff);
        Assert.False(r.UploadOff);
        Assert.Equal(10, r.UploadsPerDay);
        var targets = Audit(f).Select(e => e.Target).ToList();
        Assert.Contains("Ann Lee: download on -> off", targets);
        Assert.Contains("Ann Lee: UploadsPerDay grid -> 10", targets);
        Assert.Equal(2, targets.Count);
        Assert.False(page.QuerySelector("input#IarDownloadOn")!.HasAttribute("checked"));

        await PostIarAsync(admin, ["IarUploadOn"], new() { ["IarUploadsPerDay"] = "10" }); // nothing changes
        Assert.Equal(2, Audit(f).Count);

        var owner = await SignedInAsync(f, "owner");
        await PostIarAsync(owner, ["IarUploadOn", "IarDownloadOn"]);
        r = f.Services.GetRequiredService<IarRights>().For(AnnId);
        Assert.Equal((false, (int?)null), (r.DownloadOff, r.UploadsPerDay));
        Assert.Contains("Ann Lee: UploadsPerDay 10 -> grid", Audit(f).Select(e => e.Target));
    }

    [Fact]
    public async Task Out_of_range_override_is_refused()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        var page = await PostIarAsync(admin, ["IarUploadOn", "IarDownloadOn"], new() { ["IarUploadsPerDay"] = "500" });
        Assert.NotNull(page.QuerySelector(".error-box"));
        Assert.Null(f.Services.GetRequiredService<IarRights>().For(AnnId).UploadsPerDay);
        Assert.Empty(Audit(f));
    }

    [Fact]
    public async Task Owner_and_admin_can_set_supervisor_cannot()
    {
        await using var f = Factory();
        var supervisor = await SignedInAsync(f, "supervisor");

        Assert.Null((await PageAsync(supervisor, $"/people/{AnnId}")).QuerySelector("#person-iar form"));
        var page = await PostIarAsync(supervisor, []);

        Assert.False(f.Services.GetRequiredService<IarRights>().For(AnnId).DownloadOff);
        Assert.Contains("Supervisors can only suspend or unsuspend.", page.QuerySelector(".error-box")!.TextContent);
        Assert.Equal("refused", Assert.Single(Audit(f)).Result);
    }
}
