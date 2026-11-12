using System.Net;
using System.Net.Http.Headers;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Iar;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's and admins' inventory archive powers on /people/{id}/iar (IAR plan task 9).</summary>
public sealed class IarStaffTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid AnnId = Guid.Parse("0c000000-0000-0000-0000-00000000000c");
    private const string Reason = "Legal request from the rights holder, kept as evidence.";

    private static LauncherFactory Factory()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        { Before = TestAuth.Apply };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Lee", 0, "pass-word1", AnnId, email: "ann@example.com");
        f.Inventory.AddDefaultFolders(AnnId);
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static string Url => $"/people/{AnnId}/iar";

    private static async Task<IHtmlDocument> PageAsync(HttpClient c, string? url = null)
    {
        using var r = await c.GetAsync(url ?? Url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> TokenAsync(HttpClient c) =>
        (await PageAsync(c)).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string handler, Dictionary<string, string> form)
    {
        form["__RequestVerificationToken"] = await TokenAsync(c);
        return await c.PostAsync($"{Url}?handler={handler}", new FormUrlEncodedContent(form), Ct);
    }

    private static async Task<IReadOnlyList<IarStaffRecord>> RecordsWhenAsync(LauncherFactory f, Func<IReadOnlyList<IarStaffRecord>, bool> done)
    {
        var records = f.Services.GetRequiredService<IarStaffRecords>();
        var guard = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var all = records.For(AnnId);
            if (done(all)) return all;
            if (DateTime.UtcNow > guard) throw new TimeoutException("the staff job never finished: " + string.Join(", ", all.Select(r => r.Action)));
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, byte[] data, string name = "evidence.iar")
    {
        var content = new MultipartFormDataContent { { new StringContent(await TokenAsync(c)), "__RequestVerificationToken" } };
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "iar", name);
        return await c.PostAsync($"{Url}/upload", content, Ct);
    }

    private static byte[] Archive(Action<IarBuilder> fill)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iar");
        try
        {
            var b = new IarBuilder();
            fill(b);
            return File.ReadAllBytes(b.Save(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Supervisor_gets_403_on_page_and_endpoints()
    {
        await using var f = Factory();
        var sup = await SignedInAsync(f, "supervisor");
        using var page = await sup.GetAsync(Url, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        using var file = await sup.GetAsync($"{Url}/file/{Guid.NewGuid():N}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, file.StatusCode);
        using var up = await sup.PostAsync($"{Url}/upload", new MultipartFormDataContent(), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, up.StatusCode);
        Assert.Null((await PageAsync(await SignedInAsync(f, "supervisor"), $"/people/{AnnId}")).QuerySelector($"a[href='{Url}']"));
        Assert.NotNull((await PageAsync(await SignedInAsync(f, "admin"), $"/people/{AnnId}")).QuerySelector($"a[href='{Url}']"));
    }

    [Fact]
    public async Task Reason_required_and_case_number_checked()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        using var none = await PostAsync(admin, "Save", new() { ["Reason"] = "short" });
        Assert.Contains("at least 10", await none.Content.ReadAsStringAsync(Ct));
        using var badCase = await PostAsync(admin, "Save", new() { ["Reason"] = Reason, ["CaseNumber"] = "case 7" });
        Assert.Contains("TD-2026-001", await badCase.Content.ReadAsStringAsync(Ct));
        Assert.Empty(f.Grid.IarJobs);
        Assert.Empty(f.Services.GetRequiredService<IarStaffRecords>().For(AnnId));
    }

    [Fact]
    public async Task Staff_save_ignores_switches_limits_and_permissions()
    {
        await using var f = Factory();
        f.Services.GetRequiredService<IarRights>().Set(IarPersonRights.Default(AnnId) with { DownloadOff = true, DownloadsPerDay = 0 });
        f.Grid.OnIarJob = (r, _, _) =>
        {
            var b = new IarBuilder();
            b.Texture("Shop", "bought", IarBuilder.Copy | IarBuilder.Modify);
            b.Save(r.File);
            return Task.FromResult(1);
        };
        var admin = await SignedInAsync(f, "admin");
        using var r = await PostAsync(admin, "Save", new() { ["Reason"] = Reason });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var records = await RecordsWhenAsync(f, all => all.Any(x => x.Action == "finished"));
        var done = records.Single(x => x.Action == "finished");
        Assert.Equal(("save", Reason, 1), (done.Kind, done.Reason, done.Items));
        var path = Path.Combine(f.Paths.IarsStaff, done.File!);
        Assert.Equal(["bought"], IarArchive.Inspect(path, 1 << 20, 10).Items.Select(i => i.Name));
        Assert.Equal(new FileInfo(path).Length, done.Bytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), done.Sha256);
        Assert.Equal(IarJobKind.Save, Assert.Single(f.Grid.IarJobs).Kind);
    }

    [Fact]
    public async Task Staff_save_with_case_holds_delete_but_not_backups()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        using var r = await PostAsync(admin, "Save", new() { ["Reason"] = Reason, ["CaseNumber"] = "TD-2026-004" });
        await RecordsWhenAsync(f, all => all.Any(x => x.Action == "finished"));

        var holds = f.Services.GetRequiredService<AccountHolds>();
        Assert.Equal("This account is on hold: kept inventory evidence (TD-2026-004)", holds.Refusal(AnnId));
        Assert.False(f.Services.GetRequiredService<TakedownStore>().NamedInOpenCase(AnnId));
        Assert.Null(f.Services.GetRequiredService<IarJobs>().Check(AnnId, IarJobKind.Save) is IarRefusal.NamedInCase ? "held" : null);
    }

    [Fact]
    public async Task Staff_file_delete_needs_reason_and_lifts_hold()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        await PostAsync(admin, "Save", new() { ["Reason"] = Reason, ["CaseNumber"] = "TD-2026-004" });
        var saved = (await RecordsWhenAsync(f, all => all.Any(x => x.Action == "finished"))).Single(x => x.Action == "finished");

        using var noReason = await PostAsync(admin, "Delete", new() { ["File"] = saved.File!, ["Reason"] = "" });
        Assert.True(File.Exists(Path.Combine(f.Paths.IarsStaff, saved.File!)));

        using var ok = await PostAsync(admin, "Delete", new() { ["File"] = saved.File!, ["Reason"] = "Case closed and the evidence is no longer needed." });
        Assert.False(File.Exists(Path.Combine(f.Paths.IarsStaff, saved.File!)));
        Assert.Null(f.Services.GetRequiredService<AccountHolds>().Refusal(AnnId));
        Assert.Contains(f.Services.GetRequiredService<IarStaffRecords>().For(AnnId), x => x.Action == "deleted" && x.File == saved.File);
    }

    [Fact]
    public async Task Staff_download_of_a_kept_file_is_recorded()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        await PostAsync(admin, "Save", new() { ["Reason"] = Reason });
        var saved = (await RecordsWhenAsync(f, all => all.Any(x => x.Action == "finished"))).Single(x => x.Action == "finished");

        using var r = await admin.GetAsync($"{Url}/file/{Path.GetFileNameWithoutExtension(saved.File)}", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(saved.Bytes, (await r.Content.ReadAsByteArrayAsync(Ct)).Length);
        Assert.Contains(f.Services.GetRequiredService<IarStaffRecords>().For(AnnId), x => x.Action == "fetched");
        using var missing = await admin.GetAsync($"{Url}/file/{Guid.NewGuid():N}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Staff_upload_has_no_size_cap_but_needs_an_iar()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        using var junk = await UploadAsync(admin, "not an archive"u8.ToArray());
        Assert.Equal(HttpStatusCode.Redirect, junk.StatusCode);
        Assert.Empty(Directory.GetFiles(f.Paths.IarsIncoming));

        // Bigger than any resident limit.
        var big = Archive(b => { b.Asset(Guid.NewGuid(), "texture.jp2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(3 << 20)); b.Notecard("N", "n", "x"); });
        f.Store.Save(f.Store.LoadOrCreate() with { Iar = new IarSettings(MaxUploadMb: 1) });
        using var ok = await UploadAsync(admin, big);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains("pending=", ok.Headers.Location!.ToString());
        Assert.Single(Directory.GetFiles(f.Paths.IarsIncoming));
    }

    [Fact]
    public async Task Staff_load_shows_banned_matches_and_loads_them_when_confirmed()
    {
        await using var f = Factory();
        var asset = Guid.NewGuid();
        var holder = Guid.NewGuid();
        var row = new InventoryItemRow(asset.ToString(), 0, "Logo", "", 1, 2, 0, holder.ToString(), 3, 4, 5, 0, 6, Guid.Empty.ToString(), 0, 7,
            Guid.NewGuid(), holder, Guid.NewGuid(), 8);
        f.Services.GetRequiredService<TakedownStore>().Open(new TakedownNotice(TakedownReason.Copyright, "Rita", null, null, null, new DateOnly(2026, 11, 1), "x"),
            true, n => TakedownStoreTests.Build(n, holder) with
            {
                Items = [new CaseItem(row.ItemId, holder, "Logo", null, 0, holder.ToString(), "H", "My Inventory", asset.ToString(), row, null, null, null, DateTimeOffset.UtcNow, null)],
            });
        string[] loadedNames = [];
        f.Grid.OnIarJob = (r, _, _) => { loadedNames = [.. IarArchive.Inspect(r.File, 1 << 20, 10).Items.Select(i => i.Name)]; return Task.FromResult(2); };
        var admin = await SignedInAsync(f, "admin");
        using var up = await UploadAsync(admin, Archive(b => { b.Texture("Pics", "stolen logo", asset: asset); b.Notecard("Notes", "mine", "x"); }));
        var pending = up.Headers.Location!.ToString();

        var page = await PageAsync(admin, pending);
        Assert.Contains("stolen logo", page.QuerySelector("#iar-banned")!.TextContent);
        var name = page.QuerySelector("input[name=Pending]")!.GetAttribute("value")!;

        using var noConfirm = await PostAsync(admin, "Load", new() { ["Pending"] = name, ["Reason"] = Reason, ["Folder"] = "Evidence 1" });
        Assert.Empty(f.Grid.IarJobs);

        using var load = await PostAsync(admin, "Load", new() { ["Pending"] = name, ["Reason"] = Reason, ["Folder"] = "Evidence 1", ["Confirm"] = "true" });
        var records = await RecordsWhenAsync(f, all => all.Any(x => x.Action == "finished" && x.Kind == "load"));
        var done = records.Single(x => x.Action == "finished");
        Assert.Equal(["stolen logo"], done.BannedMatches);
        Assert.Equal("Evidence 1", done.Folder);
        Assert.Contains("stolen logo", loadedNames); // reported, not removed
        Assert.Equal("Evidence 1", Assert.Single(f.Grid.IarJobs).Folder);
        Assert.Contains(await f.Inventory.FoldersAsync(AnnId, Ct), x => x.Name == "Evidence 1");
        Assert.Empty(Directory.GetFiles(f.Paths.IarsIncoming));
    }

    [Fact]
    public async Task Every_step_writes_a_full_record_and_an_audit_line()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");
        await PostAsync(owner, "Save", new() { ["Reason"] = Reason, ["CaseNumber"] = "TD-2026-004" });
        var records = await RecordsWhenAsync(f, all => all.Any(x => x.Action == "finished"));

        Assert.Equal(["asked", "started", "finished"], records.OrderBy(r => r.At).Select(r => r.Action));
        Assert.All(records, r =>
        {
            Assert.Equal(("Test Owner", "owner", AnnId, "Ann Lee", Reason, "TD-2026-004"), (r.Actor, r.Role, r.Subject, r.SubjectName, r.Reason, r.CaseNumber));
        });
        var audit = f.Services.GetRequiredService<AuditLog>().Recent(e => e.Entry.Action == "iar-staff-save", 20).Select(e => (e.Entry.Target, e.Entry.Result)).ToList();
        Assert.Equal(3, audit.Count);
        Assert.All(audit, a => Assert.Equal("Test Owner -> Ann Lee", a.Target));
        Assert.Contains("iar-staff-save", (await PageAsync(owner, $"/people/{AnnId}")).QuerySelector("#person-history")!.TextContent);
        Assert.Contains(Reason, (await PageAsync(owner)).QuerySelector("#iar-records")!.TextContent);
    }
}
