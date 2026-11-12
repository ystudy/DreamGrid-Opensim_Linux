using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's Inventory backups page at /security/iar (IAR plan task 1).</summary>
public sealed class IarSettingsPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherFactory Factory(OwnerPlan? plan = OwnerPlan.Local)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = plan,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "owner")
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static Dictionary<string, string> Form(IarSettings i)
    {
        var form = new Dictionary<string, string>
        {
            ["MaxUploadMb"] = i.MaxUploadMb.ToString(), ["UnpackFactor"] = i.UnpackFactor.ToString(),
            ["MaxUploadItems"] = i.MaxUploadItems.ToString(), ["MaxDownloadMb"] = i.MaxDownloadMb.ToString(),
            ["DownloadsPerDay"] = i.DownloadsPerDay.ToString(), ["UploadsPerDay"] = i.UploadsPerDay.ToString(),
            ["JobsAtOnce"] = i.JobsAtOnce.ToString(), ["DownloadKbPerSecond"] = i.DownloadKbPerSecond.ToString(),
            ["LinkHours"] = i.LinkHours.ToString(), ["MinFreeDiskGb"] = i.MinFreeDiskGb.ToString(),
        };
        if (i.DownloadsOn) form["DownloadsOn"] = "true";
        if (i.UploadsOn) form["UploadsOn"] = "true";
        return form;
    }

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, IarSettings wanted)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/security/iar", Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = Form(wanted);
        form["__RequestVerificationToken"] = token;
        return await client.PostAsync("/security/iar?handler=Save", new FormUrlEncodedContent(form), Ct);
    }

    private static List<string> IarAudit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("action").GetString() == "iar-settings")
            .Select(e => e.GetProperty("target").GetString()!).ToList();
    }

    [Fact]
    public async Task Page_shows_the_current_values()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/security/iar", Ct));
        Assert.Equal("500", page.QuerySelector("input#MaxUploadMb")!.GetAttribute("value"));
        Assert.False(page.QuerySelector("input#DownloadsOn")!.HasAttribute("checked"));
        Assert.Contains("Set the limits before turning these on.", page.Body!.TextContent);
    }

    [Fact]
    public async Task Page_saves_and_audits_each_change()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);

        using var r = await SaveAsync(owner, new IarSettings(DownloadsOn: true, MaxUploadMb: 800));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var saved = f.Store.LoadOrCreate().Iar;
        Assert.True(saved.DownloadsOn);
        Assert.Equal(800, saved.MaxUploadMb);
        var audit = IarAudit(f);
        Assert.Contains("DownloadsOn off -> on", audit);
        Assert.Contains("MaxUploadMb 500 -> 800", audit);
        Assert.Equal(2, audit.Count);

        using var again = await SaveAsync(owner, saved);
        Assert.Equal(2, IarAudit(f).Count);
    }

    [Fact]
    public async Task Page_refuses_out_of_range_and_saves_nothing()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f);
        using var r = await SaveAsync(owner, new IarSettings(JobsAtOnce: 0));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, f.Store.LoadOrCreate().Iar.JobsAtOnce);
        Assert.Empty(IarAudit(f));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("supervisor")]
    public async Task Page_is_owner_only(string role)
    {
        await using var f = Factory();
        var client = await SignedInAsync(f, role);
        using var get = await client.GetAsync("/security/iar", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Fact]
    public async Task Page_is_404_without_a_guest_side()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        var owner = await SignedInAsync(f);
        using var get = await owner.GetAsync("/security/iar", Ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }
}
