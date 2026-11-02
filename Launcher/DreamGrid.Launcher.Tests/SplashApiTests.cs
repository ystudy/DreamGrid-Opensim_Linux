using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Splash;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Staff save, publish and discard the splash design through /api/splash (spec 2026-11-01-viewer-splash-design §4.1, §4.8).</summary>
public sealed class SplashApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static LauncherFactory Factory(Func<LauncherSettings, LauncherSettings>? more = null)
    {
        var f = new LauncherFactory(s => (more ?? (x => x))(s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        }))
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<HttpClient> StaffAsync(LauncherFactory f, string role)
    {
        var client = Client(f);
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    /// <summary>The antiforgery token from the dashboard, for the RequestVerificationToken header.</summary>
    private static async Task<string> TokenAsync(HttpClient client)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/dashboard", Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private static SplashDesign Design(string text, string colour = "#000000") => new(1280, 800, new SplashBackground("#223344", null, 50, 50, 0), false, 16,
    [
        new SplashBlock(1, SplashBlockKind.Text, 40, 40, 600, 80, 1, colour, 50, null, false, 8,
            new SplashFont(24, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans),
            new SplashContent(Text: new Dictionary<string, string> { ["en"] = text })),
    ]);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? token, HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body };
        if (token is not null) request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string token, SplashDesign design, int basedOn) =>
        SendAsync(client, HttpMethod.Put, "/api/splash/draft?basedOn=" + basedOn, token, JsonContent.Create(design, options: Json));

    private static string AuditText(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        // The audit log stays open while the host runs.
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task Owner_and_admin_can_save_and_publish(string role)
    {
        await using var f = Factory();
        var client = await StaffAsync(f, role);
        var token = await TokenAsync(client);

        using var saved = await SaveAsync(client, token, Design("Party at Sandbox"), 0);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, (await saved.Content.ReadFromJsonAsync<SplashDocument>(Json, Ct))!.Version);

        using var published = await SendAsync(client, HttpMethod.Post, "/api/splash/publish", token);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        using var read = await client.GetAsync("/api/splash", Ct);
        var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(1, json.GetProperty("draft").GetProperty("version").GetInt32());
        Assert.Equal(1, json.GetProperty("draftVersion").GetInt32());
        Assert.Equal(1, json.GetProperty("published").GetProperty("version").GetInt32());
        Assert.Equal("GridName", json.GetProperty("defaultDesign").GetProperty("blocks")[0].GetProperty("kind").GetString());
        Assert.Equal(SplashRules.MaxBlocks, json.GetProperty("limits").GetProperty("maxBlocks").GetInt32());
    }

    [Fact]
    public async Task Supervisor_and_anonymous_are_refused()
    {
        await using var f = Factory();
        var supervisor = await StaffAsync(f, "supervisor");
        var anonymous = Client(f);
        var body = JsonSerializer.Serialize(Design("Nope"), Json);

        foreach (var client in new[] { supervisor, anonymous })
        {
            using var get = await client.GetAsync("/api/splash", Ct);
            using var put = await SendAsync(client, HttpMethod.Put, "/api/splash/draft?basedOn=0", null, new StringContent(body, Encoding.UTF8, "application/json"));
            using var publish = await SendAsync(client, HttpMethod.Post, "/api/splash/publish", null);
            using var discard = await SendAsync(client, HttpMethod.Post, "/api/splash/discard", null);
            foreach (var r in new[] { get, put, publish, discard })
            {
                Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
                Assert.NotEqual(HttpStatusCode.NoContent, r.StatusCode);
                if (client == supervisor) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            }
        }
        Assert.False(Directory.Exists(f.Paths.Splash) && Directory.EnumerateFiles(f.Paths.Splash).Any());
    }

    [Fact]
    public async Task Api_stale_save_returns_409()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "admin");
        var token = await TokenAsync(client);
        (await SaveAsync(client, token, Design("one"), 0)).Dispose();
        (await SaveAsync(client, token, Design("two"), 1)).Dispose();

        using var stale = await SaveAsync(client, token, Design("three"), 1);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var json = JsonDocument.Parse(await stale.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(2, json.GetProperty("currentVersion").GetInt32());
    }

    [Fact]
    public async Task Invalid_design_returns_problems()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "admin");
        var token = await TokenAsync(client);

        using var bad = await SaveAsync(client, token, Design("x", colour: "red"), 0);

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problems = JsonDocument.Parse(await bad.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("problems");
        Assert.Contains(problems.EnumerateArray(), p => p.GetString()!.Contains("Block 1"));
    }

    [Fact]
    public async Task Malformed_json_is_refused()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "admin");
        var token = await TokenAsync(client);

        using var bad = await SendAsync(client, HttpMethod.Put, "/api/splash/draft?basedOn=0", token,
            new StringContent("{\"width\": \"wide\"", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Null(new SplashStore(f.Paths, TimeProvider.System).Draft());
    }

    [Fact]
    public async Task Publish_shows_on_welcome_at_once()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var token = await TokenAsync(client);
        // The built-in page is cached first.
        Assert.DoesNotContain("Fresh news", await Client(f).GetStringAsync("/welcome", Ct));

        (await SaveAsync(client, token, Design("Fresh news"), 0)).Dispose();
        (await SendAsync(client, HttpMethod.Post, "/api/splash/publish", token)).Dispose();

        Assert.Contains("Fresh news", await Client(f).GetStringAsync("/welcome", Ct));
    }

    [Fact]
    public async Task Publish_without_draft_is_409()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");

        using var r = await SendAsync(client, HttpMethod.Post, "/api/splash/publish", await TokenAsync(client));

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Publish_and_discard_are_audited()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var token = await TokenAsync(client);
        (await SaveAsync(client, token, Design("Audited"), 0)).Dispose();

        (await SendAsync(client, HttpMethod.Post, "/api/splash/publish", token)).Dispose();
        using var discard = await SendAsync(client, HttpMethod.Post, "/api/splash/discard", token);

        Assert.Equal(HttpStatusCode.NoContent, discard.StatusCode);
        var audit = AuditText(f);
        Assert.Contains("splash-published", audit);
        Assert.Contains("version 1", audit);
        Assert.Contains("splash-draft-discarded", audit);
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_refused()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "admin");

        using var put = await SendAsync(client, HttpMethod.Put, "/api/splash/draft?basedOn=0", null, JsonContent.Create(Design("x"), options: Json));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Null(new SplashStore(f.Paths, TimeProvider.System).Draft());
    }

    [Fact]
    public async Task Oversize_body_is_refused()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "admin");
        var token = await TokenAsync(client);
        var huge = "{\"width\":1280,\"pad\":\"" + new string('x', 600 * 1024) + "\"}";

        using var put = await SendAsync(client, HttpMethod.Put, "/api/splash/draft?basedOn=0", token, new StringContent(huge, Encoding.UTF8, "application/json"));

        Assert.Contains(put.StatusCode, new[] { HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest });
        Assert.Null(new SplashStore(f.Paths, TimeProvider.System).Draft());
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task Api_is_404_on_a_region_server(OwnerPlan plan)
    {
        await using var f = Factory(s => s with
        {
            Plan = plan,
            Join = plan == OwnerPlan.RegionServer
                ? new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006) : null,
            Connected = plan == OwnerPlan.Connected
                ? new ConnectedGrid("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", null) : null,
            Hypergrid = false,
        });
        var client = await StaffAsync(f, "owner");

        using var r = await client.GetAsync("/api/splash", Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
