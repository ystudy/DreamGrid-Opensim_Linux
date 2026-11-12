using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Iar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The resident's inventory backup page, its upload and its download link (IAR plan task 10).</summary>
public sealed class IarGuestPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Guest = TestAuth.GuestId;

    private static LauncherFactory Factory(IarSettings? iar = null)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Iar = iar ?? new IarSettings(DownloadsOn: true, UploadsOn: true),
        })
        { Before = TestAuth.Apply };
        TestAuth.AddStaff(f.Robust);
        f.Inventory.AddDefaultFolders(Guest);
        return f;
    }

    private static async Task<HttpClient> GuestAsync(LauncherFactory f)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false });
        using var r = await c.GetAsync("/__test/guest-signin", Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    private static async Task<IHtmlDocument> PageAsync(HttpClient c, string url = "/guest/inventory")
    {
        using var r = await c.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<IarJobSnapshot> DoneAsync(LauncherFactory f)
    {
        var jobs = f.Services.GetRequiredService<IarJobs>();
        var guard = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            if (jobs.For(Guest) is { Running: false } s) return s;
            if (DateTime.UtcNow > guard) throw new TimeoutException("the job never finished");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<IarReadyFile> BackedUpAsync(LauncherFactory f, HttpClient c)
    {
        var token = (await PageAsync(c)).QuerySelector("#iar-backup input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        using var r = await c.PostAsync("/guest/inventory?handler=Backup", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Null((await DoneAsync(f)).ErrorKey);
        return f.Services.GetRequiredService<IarFiles>().Find(Guest)!;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, byte[] data)
    {
        var token = (await PageAsync(c)).QuerySelector("#iar-upload input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var content = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "iar", "mine.iar");
        return await c.PostAsync("/guest/inventory/upload", content, Ct);
    }

    [Fact]
    public async Task Page_404_when_both_off()
    {
        await using var f = Factory(new IarSettings());
        var c = await GuestAsync(f);
        using var r = await c.GetAsync("/guest/inventory", Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null((await PageAsync(c, "/guest")).QuerySelector("a[href='/guest/inventory']"));
    }

    [Fact]
    public async Task Only_allowed_part_shows()
    {
        await using var f = Factory(new IarSettings(DownloadsOn: true));
        var c = await GuestAsync(f);
        var page = await PageAsync(c);
        Assert.NotNull(page.QuerySelector("#iar-backup"));
        Assert.Null(page.QuerySelector("#iar-upload"));
        Assert.NotNull((await PageAsync(c, "/guest")).QuerySelector("a[href='/guest/inventory']"));

        f.Services.GetRequiredService<IarRights>().Set(IarPersonRights.Default(Guest) with { DownloadOff = true });
        using var gone = await c.GetAsync("/guest/inventory", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Named_in_case_sees_not_available()
    {
        await using var f = Factory();
        f.Services.GetRequiredService<TakedownStore>().Open(new TakedownNotice(TakedownReason.Rules, "Staff", null, null, null, new DateOnly(2026, 11, 1), "x"),
            false, n => TakedownStoreTests.Build(n, Guest));
        var page = await PageAsync(await GuestAsync(f));
        Assert.Equal("Inventory backups are not available on your account right now.", page.QuerySelector("#iar-not-available")!.TextContent.Trim());
        Assert.Null(page.QuerySelector("#iar-backup form"));
    }

    [Fact]
    public async Task Left_out_table_lists_name_folder_reason()
    {
        await using var f = Factory();
        f.Grid.OnIarJob = (r, _, _) =>
        {
            var b = new IarBuilder();
            b.Notecard("Notes", "mine", "x");
            b.Texture("Shop/Dresses", "red dress", IarBuilder.Copy | IarBuilder.Modify);
            b.Save(r.File);
            return Task.FromResult(2);
        };
        var c = await GuestAsync(f);
        await BackedUpAsync(f, c);
        var page = await PageAsync(c);
        var row = page.QuerySelectorAll("#iar-left-out tbody tr").Single();
        Assert.Equal(["red dress", "Shop/Dresses", "You can't transfer it"], row.QuerySelectorAll("td").Select(td => td.TextContent));
        Assert.NotNull(page.QuerySelector("#iar-download"));
    }

    [Fact]
    public async Task Download_supports_ranges_and_is_audited()
    {
        await using var f = Factory();
        var c = await GuestAsync(f);
        var ready = await BackedUpAsync(f, c);
        using var all = await c.GetAsync($"/guest/inventory/file/{ready.Token}", Ct);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.Equal(ready.Bytes, (await all.Content.ReadAsByteArrayAsync(Ct)).Length);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/guest/inventory/file/{ready.Token}");
        req.Headers.Range = new RangeHeaderValue(0, 9);
        using var part = await c.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(10, (await part.Content.ReadAsByteArrayAsync(Ct)).Length);
        Assert.Contains(f.Services.GetRequiredService<DreamGrid.Launcher.Core.Logs.AuditLog>().Recent(e => e.Entry.Action == "iar-download-fetched", 5), _ => true);
    }

    [Fact]
    public async Task Link_refused_once_the_resident_is_named_in_an_open_case()
    {
        await using var f = Factory();
        var c = await GuestAsync(f);
        var ready = await BackedUpAsync(f, c);
        f.Services.GetRequiredService<TakedownStore>().Open(new TakedownNotice(TakedownReason.Rules, "Staff", null, null, null, new DateOnly(2026, 11, 1), "x"),
            false, n => TakedownStoreTests.Build(n, Guest));
        using var r = await c.GetAsync($"/guest/inventory/file/{ready.Token}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Link_refused_for_other_resident_unknown_token_and_after_stamp_changes()
    {
        await using var f = Factory();
        var c = await GuestAsync(f);
        var ready = await BackedUpAsync(f, c);
        using var unknown = await c.GetAsync("/guest/inventory/file/not-a-token", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var stranger = f.CreateClient(new() { AllowAutoRedirect = false });
        using var anon = await stranger.GetAsync($"/guest/inventory/file/{ready.Token}", Ct);
        Assert.NotEqual(HttpStatusCode.OK, anon.StatusCode);

        f.Services.GetRequiredService<GuestStamps>().Rotate(Guest); // suspended or signed out everywhere
        using var after = await (await GuestAsync(f)).GetAsync($"/guest/inventory/file/{ready.Token}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Upload_over_cap_cut_off_and_part_file_removed()
    {
        await using var f = Factory(new IarSettings(UploadsOn: true, MaxUploadMb: 1));
        var c = await GuestAsync(f);
        var big = new byte[3 << 20];
        new Random(1).NextBytes(big);
        using var r = await UploadAsync(c, big);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("problem=TooBig", r.Headers.Location!.ToString());
        Assert.Empty(Directory.GetFiles(f.Paths.IarsIncoming));
        Assert.Empty(f.Grid.IarJobs);
        var page = await PageAsync(c, r.Headers.Location!.ToString());
        Assert.Contains("The file is bigger than 1 MB.", page.QuerySelector(".error-box")!.TextContent);
    }

    [Fact]
    public async Task Upload_not_iar_refused_before_job()
    {
        await using var f = Factory();
        var c = await GuestAsync(f);
        using var r = await UploadAsync(c, "hello"u8.ToArray());
        Assert.Contains("problem=NotAnIar", r.Headers.Location!.ToString());
        Assert.Empty(f.Grid.IarJobs);
        Assert.Empty(Directory.GetFiles(f.Paths.IarsIncoming));
    }

    [Fact]
    public async Task Upload_loads_into_a_new_folder()
    {
        await using var f = Factory();
        var c = await GuestAsync(f);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iar");
        var b = new IarBuilder();
        b.Notecard("Notes", "n", "x");
        b.Save(path);
        using var r = await UploadAsync(c, File.ReadAllBytes(path));
        File.Delete(path);
        Assert.Equal("/guest/inventory", r.Headers.Location!.ToString());
        var s = await DoneAsync(f);
        Assert.Equal(IarFolderNames.Imported(DateTimeOffset.UtcNow), s.Folder);
        Assert.Contains("relog", (await PageAsync(c)).QuerySelector("#iar-job")!.TextContent);
    }

    [Fact]
    public async Task ThrottledStream_waits_by_the_rate()
    {
        Assert.Equal(TimeSpan.Zero, ThrottledStream.WaitFor(0, TimeSpan.Zero, 1000));
        Assert.Equal(TimeSpan.FromSeconds(2), ThrottledStream.WaitFor(2000, TimeSpan.Zero, 1000));
        Assert.Equal(TimeSpan.FromSeconds(1), ThrottledStream.WaitFor(2000, TimeSpan.FromSeconds(1), 1000));
        Assert.Equal(TimeSpan.Zero, ThrottledStream.WaitFor(2000, TimeSpan.FromSeconds(3), 1000));

        var started = DateTime.UtcNow;
        await using var s = new ThrottledStream(new MemoryStream(new byte[128 * 1024]), 64 * 1024, TimeProvider.System);
        await s.CopyToAsync(Stream.Null, Ct);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(1.4), "128 KB at 64 KB/s took under 1.5 s");
        Assert.True(s.CanSeek);
    }

    [Fact]
    public void Every_key_has_six_languages()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var sources = new[] { "DreamGrid.Launcher/Pages/Guest/Inventory.cshtml", "DreamGrid.Launcher/Pages/Guest/Inventory.cshtml.cs", "DreamGrid.Launcher/Iar/IarJobs.cs" }
            .Select(p => File.ReadAllText(Path.Combine(root, p))).ToList();
        var keys = sources.SelectMany(s => Regex.Matches(s, "\"(Iar\\.[A-Za-z.]+[A-Za-z])\"").Select(m => m.Groups[1].Value))
            .Concat(Enum.GetNames<IarRefusal>().Select(n => "Iar.Refused." + n))
            .Concat(Enum.GetNames<LeftOutReason>().Select(n => "Iar.LeftOut." + n))
            .Where(k => !k.EndsWith('.')).ToHashSet();
        Assert.True(keys.Count > 30);
        foreach (var lang in new[] { "", ".de", ".es", ".fr", ".pt", ".ru" })
        {
            var names = XDocument.Load(Path.Combine(root, "DreamGrid.Launcher", "Resources", $"ResidentText{lang}.resx"))
                .Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();
            Assert.Empty(keys.Where(k => !names.Contains(k)).Select(k => lang + ":" + k));
        }
    }

    [Fact]
    public async Task Page_shows_no_raw_keys()
    {
        await using var f = Factory();
        var text = (await PageAsync(await GuestAsync(f))).Body!.TextContent;
        Assert.DoesNotMatch(new Regex(@"Iar\.[A-Z]"), text);
        Assert.Contains("Only items you can copy, modify and transfer go in the file. Everything else stays on Avalon.", text);
    }
}
