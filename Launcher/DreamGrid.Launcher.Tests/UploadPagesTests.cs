using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The guest's model upload: the form, the check report, Make the item, the done page and the Limits page.</summary>
public sealed class UploadPagesTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("51000000-0000-0000-0000-000000000001");
    private static readonly Guid TextureId = Guid.Parse("2b7e9d40-5c1a-4f63-8e2d-91a0c4b7f5d3");
    private static readonly Guid MeshId = Guid.Parse("8d3f1a52-7e6b-4c09-a4f1-3c5e2b9d7a68");
    private static readonly Guid ObjectId = Guid.Parse("e4a90c17-2f8d-4b35-9d6e-7a1c5f0b3e24");
    private static readonly Guid SecondObjectId = Guid.Parse("e4a90c17-2f8d-4b35-9d6e-7a1c5f0b3e25");
    private const string DoneLine = "Castle is in your inventory, in the Uploads folder. If you're logged in, log out and back in to see it.";

    private readonly ConcurrentQueue<string> _calls = new();
    private readonly FakeConverter _converter;
    private readonly FakeAssets _assets;
    private readonly FakeCharge _charge;
    private LauncherFactory _f;

    public UploadPagesTests()
    {
        _converter = new FakeConverter();
        _assets = new FakeAssets(_calls);
        _charge = new FakeCharge(_calls);
        _f = Factory(OwnerPlan.Local);
    }

    private LauncherFactory Factory(OwnerPlan plan, int? maxPart = null, bool regions = true) =>
        new(s => s with
        {
            SetupToken = null,
            Plan = plan,
            Setup = s.Setup with { OwnerCreated = true, Finished = true },
            Programs = [new ProgramSettings("Home", 8004, 8011, "ubODE")],
            Regions = regions
                ? [new RegionSettings("Home", "Home", HomeId, UdpPort: 8004, Options: maxPart is null ? null : new RegionOptions(MaxNonPhysicalSize: maxPart))]
                : [],
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services =>
            {
                services.RemoveAll<IModelConverter>();
                services.AddSingleton<IModelConverter>(_converter);
                services.RemoveAll<IRobustAssets>();
                services.AddSingleton<IRobustAssets>(_assets);
                services.RemoveAll<IUploadCharge>();
                services.AddSingleton<IUploadCharge>(_charge);
            },
        };

    /// <summary>Starts again on another plan, with the same fakes.</summary>
    private async Task UseAsync(OwnerPlan plan, int? maxPart = null, bool regions = true)
    {
        await _f.DisposeAsync();
        _f = Factory(plan, maxPart, regions);
        TestAuth.AddStaff(_f.Robust);
    }

    public ValueTask InitializeAsync()
    {
        TestAuth.AddStaff(_f.Robust);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ImportJobs Jobs => _f.Services.GetRequiredService<ImportJobs>();

    private void Allow() => _f.Services.GetRequiredService<UploadRights>().Grant(TestAuth.GuestId, "Test Guest");

    private async Task<HttpClient> GuestAsync()
    {
        var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        using var r = await client.GetAsync("/__test/guest-signin", Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static async Task<IHtmlDocument> DocAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<IHtmlDocument> DocAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Text(IHtmlDocument doc) => doc.Body!.TextContent;

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var token = (await DocAsync(client, "/guest")).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    /// <summary>Posts the upload form with <paramref name="fileName"/> and returns the response (not followed).</summary>
    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string fileName = "castle.glb", string? itemName = null)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(await TokenAsync(client)), "__RequestVerificationToken" },
            { new ByteArrayContent("glTF fake model"u8.ToArray()), "File", fileName },
        };
        if (itemName is not null) content.Add(new StringContent(itemName), "ItemName");
        return await client.PostAsync("/guest/upload", content, Ct);
    }

    /// <summary>Uploads and returns the job id the form redirected to.</summary>
    private static async Task<string> UploadedJobAsync(HttpClient client, string fileName = "castle.glb", string? itemName = null)
    {
        using var r = await UploadAsync(client, fileName, itemName);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var location = r.Headers.Location!.OriginalString;
        Assert.StartsWith("/guest/upload/", location);
        return location["/guest/upload/".Length..];
    }

    /// <summary>Posts the report page's form for <paramref name="handler"/> with the fields the page wrote, changed by <paramref name="change"/>.</summary>
    private static async Task<HttpResponseMessage> PostReportAsync(HttpClient client, string job, string handler,
        Action<Dictionary<string, string>>? change = null)
    {
        var doc = await DocAsync(client, "/guest/upload/" + job);
        var form = doc.QuerySelector("form.upload-report")!;
        var fields = new Dictionary<string, string>();
        foreach (var input in form.QuerySelectorAll("input"))
        {
            var name = input.GetAttribute("name");
            if (name is null) continue;
            if (input.GetAttribute("type") == "checkbox" && !input.HasAttribute("checked")) continue;
            fields[name] = input.GetAttribute("value") ?? "";
        }
        change?.Invoke(fields);
        return await client.PostAsync($"/guest/upload/{job}?handler={handler}", new FormUrlEncodedContent(fields), Ct);
    }

    /// <summary>The report form's fields for a castle checked as uploaded, with a fresh token.</summary>
    private static async Task<Dictionary<string, string>> MakeFieldsAsync(HttpClient client) => new()
    {
        ["__RequestVerificationToken"] = await TokenAsync(client), ["ItemName"] = "Castle", ["ScalePercent"] = "100",
        ["Copy"] = "true", ["Modify"] = "true", ["Transfer"] = "true",
    };

    private string JobDir(string job) => Path.Combine(_f.Paths.Uploads, job);

    private List<JsonElement> Audit(string action)
    {
        var path = Path.Combine(_f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(e => e.GetProperty("action").GetString() == action).ToList();
    }

    private static Finding F(string code, FindingKind kind, params (string Key, string Value)[] values) =>
        new(code, kind, values.ToDictionary(v => v.Key, v => v.Value));

    /// <summary>The castle with one safe fix, two risky findings and a part that is too big.</summary>
    private static ImportReport BlockedCastle() => new(1, true, new ReportSummary(3, 24, 2_100_000, 4, [300, 20, 12.5], 85),
    [
        F("split-materials", FindingKind.Safe, ("materials", "20"), ("perPart", "8"), ("parts", "3")),
        F("heavy-mesh", FindingKind.Risky, ("triangles", "2100000"), ("threshold", "200000")),
        F("inside-out", FindingKind.Risky, ("parts", "1"), ("part", "Gate")),
        F("too-big", FindingKind.Blocking, ("part", "Tower"), ("side", "300"), ("limit", "256"), ("fitPercent", "85")),
    ]);

    /// <summary>The castle that can be made: one safe fix and one risky finding.</summary>
    private static ImportReport GoodCastle() => new(1, false, new ReportSummary(3, 24, 250_000, 1, [10, 8, 6], null),
    [
        F("split-materials", FindingKind.Safe, ("materials", "20"), ("perPart", "8"), ("parts", "3")),
        F("heavy-mesh", FindingKind.Risky, ("triangles", "250000"), ("threshold", "200000")),
    ]);

    // ---- visibility ----

    [Fact]
    public async Task Upload_hidden_without_right()
    {
        using var client = await GuestAsync();
        foreach (var url in new[] { "/guest/upload", "/guest/upload/limits", "/guest/upload/" + new string('a', 32) })
        {
            using var r = await client.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        using var post = await client.PostAsync("/guest/upload", new MultipartFormDataContent(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Null((await DocAsync(client, "/guest")).QuerySelector("a[href='/guest/upload']"));

        Allow();
        Assert.NotNull((await DocAsync(client, "/guest")).QuerySelector("a[href='/guest/upload']"));
        Assert.Equal("Upload a model", (await DocAsync(client, "/guest")).QuerySelector("a[href='/guest/upload']")!.TextContent.Trim());
    }

    [Fact]
    public async Task Upload_hidden_on_region_server()
    {
        // A Grid Asset Server with no region of its own has no limits to check against.
        await UseAsync(OwnerPlan.GridAsset, regions: false);
        Allow();
        using var client = await GuestAsync();
        using (var r = await client.GetAsync("/guest/upload", Ct))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null((await DocAsync(client, "/guest")).QuerySelector("a[href='/guest/upload']"));

        // A Region Server has no guest pages at all.
        await UseAsync(OwnerPlan.RegionServer);
        Allow();
        var regionServer = _f.CreateClient(new() { AllowAutoRedirect = false });
        using (await regionServer.GetAsync("/__test/guest-signin", Ct)) { }
        using (var r = await regionServer.GetAsync("/guest/upload", Ct))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await regionServer.GetAsync("/guest/upload/limits", Ct))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Upload_form_has_the_file_input_and_token()
    {
        Allow();
        using var client = await GuestAsync();
        var doc = await DocAsync(client, "/guest/upload");
        var file = doc.QuerySelector("form input[type=file][name=File]");
        Assert.NotNull(file);
        Assert.Equal(".glb,.gltf", file.GetAttribute("accept"));
        Assert.NotNull(doc.QuerySelector("form input[name=ItemName]"));
        Assert.NotNull(doc.QuerySelector("form input[name=__RequestVerificationToken]"));
        Assert.NotNull(doc.QuerySelector("a[href='/guest/upload/limits']"));
    }

    // ---- the check and the report ----

    [Fact]
    public async Task Upload_shows_report()
    {
        Allow();
        _converter.Report = BlockedCastle();
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");
        Assert.Matches("^[0-9a-f]{32}$", job);

        var doc = await DocAsync(client, "/guest/upload/" + job);
        var text = Text(doc);
        Assert.Contains("castle.glb was checked", text);
        var safe = doc.QuerySelector(".finding-split-materials")!;
        Assert.Contains("Split into 3 linked parts (20 materials, 8 per part). This won't change how it looks.", safe.TextContent);

        var warning = doc.QuerySelector(".warning-box")!;
        var heading = warning.QuerySelector(".warning-heading")!;
        Assert.Equal("Castle may look distorted or shaped wrong.", heading.QuerySelector("strong")!.TextContent.Trim());
        Assert.Equal("Castle may look distorted or shaped wrong. You should correct:", heading.TextContent.Trim());
        var risky = warning.QuerySelectorAll("ol > li").Select(li => li.ClassList.First(c => c.StartsWith("finding-", StringComparison.Ordinal))).ToList();
        Assert.Equal(["finding-heavy-mesh", "finding-inside-out"], risky);

        var big = doc.QuerySelector(".blocking .finding-too-big")!;
        Assert.Equal("85", big.QuerySelector("input[name=ScalePercent]")!.GetAttribute("value"));
        Assert.Null(doc.QuerySelector("button.make-item"));
        Assert.DoesNotContain("Make the item", text);
        Assert.NotNull(doc.QuerySelector("a[href='/guest/upload/limits']"));

        // What the converter was given.
        var options = Assert.Single(_converter.Seen);
        Assert.Equal(new ImportOptions("Castle", "castle.glb", 100, new NextOwner(true, true, true), TestAuth.GuestId, "Test Guest"), options);
        Assert.EndsWith(Path.Combine(job, "input.glb"), Assert.Single(_converter.Inputs));
    }

    [Fact]
    public async Task Report_offers_make_the_item_with_next_owner_ticks_on()
    {
        Allow();
        _converter.Report = GoodCastle();
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");
        var doc = await DocAsync(client, "/guest/upload/" + job);
        Assert.Equal("Make the item", doc.QuerySelector("button.make-item")!.TextContent.Trim());
        foreach (var tick in new[] { "Copy", "Modify", "Transfer" })
            Assert.True(doc.QuerySelector($"input[type=checkbox][name={tick}]")!.HasAttribute("checked"), tick);
        Assert.Equal("Castle", doc.QuerySelector("input[name=ItemName]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task Rescale_runs_the_check_again_with_the_percent()
    {
        Allow();
        _converter.Report = BlockedCastle();
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");

        _converter.Report = GoodCastle();
        using var r = await PostReportAsync(client, job, "Rescale");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var again = r.Headers.Location!.OriginalString["/guest/upload/".Length..];

        Assert.Equal(2, _converter.Seen.Count);
        Assert.Equal(85, _converter.Seen[1].ScalePercent);
        Assert.Equal("Castle", _converter.Seen[1].ItemName);
        Assert.NotEqual(job, again);
        Assert.False(Directory.Exists(JobDir(job)));
        Assert.True(Directory.Exists(JobDir(again)));
        // The new job checked the same file.
        Assert.Equal("glTF fake model", await File.ReadAllTextAsync(_converter.Inputs[1], Ct));
        Assert.Equal("85", (await DocAsync(client, "/guest/upload/" + again)).QuerySelector("input[name=ScalePercent]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task Converter_failed_says_couldnt_read()
    {
        Allow();
        using var client = await GuestAsync();
        foreach (var outcome in new[] { ConverterOutcome.Failed, ConverterOutcome.TimedOut })
        {
            _converter.Outcome = outcome;
            using var r = await UploadAsync(client);
            var doc = await DocAsync(r);
            Assert.Contains("We couldn't read this file", Text(doc));
        }
        Assert.Empty(Directory.Exists(_f.Paths.Uploads) ? Directory.GetDirectories(_f.Paths.Uploads) : []);
        Assert.True(Jobs.TryEnterSlot());
        Jobs.LeaveSlot();
    }

    [Fact]
    public async Task Busy_slot_says_try_again()
    {
        Allow();
        using var client = await GuestAsync();
        Assert.True(Jobs.TryEnterSlot());
        try
        {
            using var r = await UploadAsync(client);
            Assert.Contains("The uploader is busy, try again in a minute", Text(await DocAsync(r)));
            Assert.Empty(_converter.Seen);
        }
        finally { Jobs.LeaveSlot(); }
    }

    [Fact]
    public async Task Converter_that_throws_leaves_the_slot_free()
    {
        Allow();
        using var client = await GuestAsync();
        foreach (var e in new Exception[] { new InvalidOperationException("boom"), new OperationCanceledException() })
        {
            _converter.Throw = e;
            using var r = await UploadAsync(client);
            Assert.Contains("We couldn't read this file", Text(await DocAsync(r)));
            Assert.True(Jobs.TryEnterSlot(), e.GetType().Name);
            Jobs.LeaveSlot();
        }
        Assert.Empty(Directory.GetDirectories(_f.Paths.Uploads));
        Assert.Contains(_f.Logs, l => l.Level == LogLevel.Error && l.Message.Contains("boom"));
    }

    [Fact]
    public async Task Wrong_kind_of_file_is_refused_before_the_check()
    {
        Allow();
        using var client = await GuestAsync();
        using var r = await UploadAsync(client, "castle.fbx");
        Assert.Contains("Choose a .glb or .gltf file", Text(await DocAsync(r)));
        Assert.Empty(_converter.Seen);
    }

    [Fact]
    public async Task Hostile_file_name_is_cleaned()
    {
        Allow();
        _converter.Report = GoodCastle();
        using var client = await GuestAsync();
        var name = "../../evil<script>\u0007🙂" + new string('a', 300) + ".glb";
        var job = await UploadedJobAsync(client, name);

        Assert.Matches("^[0-9a-f]{32}$", job);
        Assert.Equal(["input.glb"], Directory.GetFiles(JobDir(job)).Select(Path.GetFileName)
            .Where(f => f!.StartsWith("input", StringComparison.Ordinal)));
        var expected = ("evilscript🙂" + new string('a', 300))[..63];
        var options = Assert.Single(_converter.Seen);
        Assert.Equal(expected, options.ItemName);
        Assert.Equal(expected, options.FileName);

        var html = await client.GetStringAsync("/guest/upload/" + job, Ct);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("../", html);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.Equal(expected, doc.QuerySelector("input[name=ItemName]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task Another_guests_job_is_404()
    {
        Allow();
        var stranger = Jobs.Create(Guid.NewGuid());
        File.WriteAllText(Path.Combine(stranger, "report.json"), JsonSerializer.Serialize(GoodCastle(), ContractJson.Options));
        var job = Path.GetFileName(stranger);
        using var client = await GuestAsync();

        using (var r = await client.GetAsync("/guest/upload/" + job, Ct))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var token = await TokenAsync(client);
        foreach (var handler in new[] { "Make", "Rescale" })
        {
            using var r = await client.PostAsync($"/guest/upload/{job}?handler={handler}",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["ItemName"] = "Mine" }), Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        Assert.Empty(_calls);
        Assert.Empty(_converter.Seen);
        Assert.True(Directory.Exists(stranger));
    }

    // ---- Make the item ----

    [Fact]
    public async Task Make_charges_once_then_writes_then_audits()
    {
        Allow();
        _converter.Report = GoodCastle();
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");

        using var r = await PostReportAsync(client, job, "Make");
        var doc = await DocAsync(r);

        Assert.Equal(["charge", $"store:{TextureId}:0", $"store:{MeshId}:49", $"store:{ObjectId}:6", "folder", "item:Castle"], _calls.ToArray());
        Assert.Equal(TestAuth.GuestId, _charge.Guests.Single());
        Assert.Equal(TestAuth.GuestId, _assets.Items.Single().Owner);
        Assert.Equal(ObjectId, _assets.Items.Single().Item.AssetId);
        Assert.Contains(DoneLine, Text(doc));
        Assert.NotNull(doc.QuerySelector("a[href='/guest/upload']"));
        // The report is repeated below.
        Assert.NotNull(doc.QuerySelector(".finding-split-materials"));
        Assert.False(Directory.Exists(JobDir(job)));

        var audit = Assert.Single(Audit("model-uploaded"));
        Assert.Equal("Test Guest", audit.GetProperty("actor").GetString());
        Assert.Equal(TestAuth.GuestId.ToString(), audit.GetProperty("actorId").GetString());
        var target = audit.GetProperty("target").GetString()!;
        Assert.Contains("Castle", target);
        Assert.Contains("castle.glb", target);
        foreach (var id in new[] { TextureId, MeshId, ObjectId })
            Assert.Contains(id.ToString(), target);
        Assert.Equal("ok", audit.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Charge_refused_stops_before_writing()
    {
        Allow();
        _converter.Report = GoodCastle();
        _charge.Refuse = "You need 10 more coins for this upload.";
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");

        using var r = await PostReportAsync(client, job, "Make");
        var doc = await DocAsync(r);

        Assert.Equal(["charge"], _calls.ToArray());
        Assert.Contains("You need 10 more coins for this upload.", Text(doc));
        Assert.Empty(Audit("model-uploaded"));
        // Still there, so the guest can try again.
        Assert.True(Directory.Exists(JobDir(job)));
        Assert.False(File.Exists(Path.Combine(JobDir(job), "done")));
    }

    [Fact]
    public async Task Make_the_item_twice_makes_one_item()
    {
        Allow();
        _converter.Report = GoodCastle();
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");
        var fields = await MakeFieldsAsync(client);

        using var first = await client.PostAsync($"/guest/upload/{job}?handler=Make", new FormUrlEncodedContent(fields), Ct);
        Assert.Contains(DoneLine, Text(await DocAsync(first)));
        using var second = await client.PostAsync($"/guest/upload/{job}?handler=Make", new FormUrlEncodedContent(fields), Ct);
        Assert.Contains(DoneLine, Text(await DocAsync(second)));

        Assert.Single(_calls, c => c == "charge");
        Assert.Single(_assets.Items);
        Assert.Single(Audit("model-uploaded"));
    }

    [Fact]
    public async Task Make_while_the_first_is_still_running_writes_nothing()
    {
        Allow();
        _converter.Report = GoodCastle();
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");
        var fields = await MakeFieldsAsync(client);
        // The first request has marked the job and is still writing.
        File.WriteAllText(Path.Combine(JobDir(job), "done"), "");

        using var r = await client.PostAsync($"/guest/upload/{job}?handler=Make", new FormUrlEncodedContent(fields), Ct);
        Assert.Contains("This upload is already finished", Text(await DocAsync(r)));
        var page = await DocAsync(client, "/guest/upload/" + job);
        Assert.Contains("This upload is already finished", Text(page));
        Assert.Null(page.QuerySelector("button.make-item"));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Changed_name_or_ticks_are_checked_again_before_making()
    {
        Allow();
        _converter.Report = GoodCastle();
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");

        using var r = await PostReportAsync(client, job, "Make", f => { f["ItemName"] = "Keep"; f.Remove("Transfer"); });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Empty(_calls);
        Assert.Equal(new NextOwner(true, true, false), _converter.Seen[1].NextOwner);
        Assert.Equal("Keep", _converter.Seen[1].ItemName);
        var again = r.Headers.Location!.OriginalString["/guest/upload/".Length..];
        var doc = await DocAsync(client, "/guest/upload/" + again);
        Assert.Contains("We checked it again with your changes", Text(doc));
        Assert.False(doc.QuerySelector("input[name=Transfer]")!.HasAttribute("checked"));
    }

    [Fact]
    public async Task Robust_down_shows_nothing_charged()
    {
        Allow();
        _converter.Report = GoodCastle();
        _assets.Fail = new RobustAssetsUnavailableException("Robust could not be reached or its reply was not understood.");
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");

        using var r = await PostReportAsync(client, job, "Make");
        Assert.Contains("That didn't work, nothing was charged", Text(await DocAsync(r)));
        Assert.Contains(_f.Logs, l => l.Level == LogLevel.Error && l.Message.Contains("Robust could not be reached"));
        Assert.False(Directory.Exists(JobDir(job)));
        Assert.Empty(_assets.Items);
        Assert.Empty(Audit("model-uploaded"));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("outside")]
    [InlineData("missing")]
    [InlineData("two-objects")]
    [InlineData("creator")]
    [InlineData("too-large")]
    public async Task Bad_manifest_writes_nothing(string fault)
    {
        Allow();
        _converter.Report = GoodCastle();
        _converter.Fault = fault;
        using var client = await GuestAsync();
        var job = await UploadedJobAsync(client, itemName: "Castle");

        using var r = await PostReportAsync(client, job, "Make");
        Assert.Contains("That didn't work, nothing was charged", Text(await DocAsync(r)));
        Assert.Empty(_calls);
        Assert.False(Directory.Exists(JobDir(job)));
    }

    // ---- the Limits page ----

    [Fact]
    public async Task Limits_page_lists_this_grids_values()
    {
        Allow();
        using var client = await GuestAsync();
        var text = Text(await DocAsync(client, "/guest/upload/limits"));
        Assert.Contains("256 m", text);
        Assert.Contains("8 faces", text);
        Assert.Contains("1024 px", text);
        Assert.Contains("64 MB", text);
        Assert.Contains("Blender", text);
        Assert.DoesNotContain("joined the grid", text);

        await UseAsync(OwnerPlan.GridAsset, maxPart: 64);
        Allow();
        using var grid = await GuestAsync();
        text = Text(await DocAsync(grid, "/guest/upload/limits"));
        Assert.Contains("64 m", text);
        Assert.DoesNotContain("256 m", text);
        Assert.Contains("Regions on computers that joined the grid aren't checked", text);
    }

    // ---- fakes ----

    /// <summary>Writes the report (and, unless it blocks, the assets and manifest) the way the real converter does.</summary>
    private sealed class FakeConverter : IModelConverter
    {
        public List<ImportOptions> Seen { get; } = [];
        public List<string> Inputs { get; } = [];
        public ImportReport Report { get; set; } = new(1, false, new ReportSummary(1, 1, 12, 0, [1, 1, 1], null), []);
        public ConverterOutcome Outcome { get; set; } = ConverterOutcome.Done;
        public Exception? Throw { get; set; }
        /// <summary>A fault written into the manifest, for the launcher's own checks.</summary>
        public string? Fault { get; set; }

        public async Task<ConverterRun> CheckAsync(string inputFile, ImportLimits limits, ImportOptions options, string jobDir, CancellationToken ct)
        {
            Seen.Add(options);
            Inputs.Add(inputFile);
            if (Throw is { } e) throw e;
            if (Outcome != ConverterOutcome.Done) return new ConverterRun(Outcome, null);
            await File.WriteAllTextAsync(Path.Combine(jobDir, "report.json"), JsonSerializer.Serialize(Report, ContractJson.Options), ct);
            if (!Report.Blocked) await WriteAssetsAsync(options, jobDir, ct);
            return new ConverterRun(ConverterOutcome.Done, Report);
        }

        private async Task WriteAssetsAsync(ImportOptions options, string jobDir, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.Combine(jobDir, "assets"));
            var assets = new List<ManifestAsset>
            {
                new(TextureId, Fault == "type" ? 20 : 0, options.ItemName + " texture 1", $"assets/{TextureId}.bin"),
                new(MeshId, 49, options.ItemName + " mesh 1", Fault == "outside" ? "../../escape.bin" : $"assets/{MeshId}.bin"),
                new(ObjectId, 6, options.ItemName, $"assets/{ObjectId}.bin"),
            };
            if (Fault == "two-objects") assets.Insert(0, new ManifestAsset(SecondObjectId, 6, "Another", $"assets/{SecondObjectId}.bin"));
            foreach (var a in assets.Where(a => a.File.StartsWith("assets/", StringComparison.Ordinal)))
            {
                if (Fault == "missing" && a.Id == MeshId) continue;
                var bytes = Fault == "too-large" && a.Id == TextureId ? new byte[64 * 1024 * 1024 + 1] : [1, 2, 3];
                await File.WriteAllBytesAsync(Path.Combine(jobDir, a.File), bytes, ct);
            }
            var creator = Fault == "creator" ? Guid.NewGuid() : options.UploaderId;
            var item = new ManifestItem(ObjectId, options.ItemName, $"Uploaded from {options.FileName} on 2026-11-10", 0x8e000, creator);
            await File.WriteAllTextAsync(Path.Combine(jobDir, "manifest.json"),
                JsonSerializer.Serialize(new ImportManifest(1, assets, item), ContractJson.Options), ct);
        }
    }

    private sealed class FakeAssets(ConcurrentQueue<string> calls) : IRobustAssets
    {
        public static readonly Guid Folder = Guid.Parse("52000000-0000-0000-0000-000000000001");
        public List<(Guid Owner, Guid Folder, ManifestItem Item)> Items { get; } = [];
        public Exception? Fail { get; set; }

        public Task StoreAsync(Guid id, int type, string name, byte[] data, Guid creator, CancellationToken ct)
        {
            if (Fail is { } e) throw e;
            calls.Enqueue($"store:{id}:{type}");
            return Task.CompletedTask;
        }

        public Task<Guid> UploadsFolderAsync(Guid owner, CancellationToken ct)
        {
            calls.Enqueue("folder");
            return Task.FromResult(Folder);
        }

        public Task AddItemAsync(Guid owner, Guid folder, ManifestItem item, CancellationToken ct)
        {
            calls.Enqueue("item:" + item.Name);
            Items.Add((owner, folder, item));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCharge(ConcurrentQueue<string> calls) : IUploadCharge
    {
        public List<Guid> Guests { get; } = [];
        public string? Refuse { get; set; }

        public Task<ChargeResult> TryChargeAsync(Guid guest, ImportReport report, CancellationToken ct)
        {
            calls.Enqueue("charge");
            Guests.Add(guest);
            return Task.FromResult(Refuse is null ? new ChargeResult(true, null) : new ChargeResult(false, Refuse));
        }
    }
}
