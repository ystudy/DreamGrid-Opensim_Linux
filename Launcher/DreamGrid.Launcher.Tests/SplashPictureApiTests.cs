using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Pictures;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Splash;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Staff upload, list and delete splash pictures; residents get only the published page's (spec §4.4).</summary>
public sealed class SplashPictureApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static LauncherFactory Factory(Action<IServiceCollection>? extra = null)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = extra,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    internal static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    internal static async Task<HttpClient> StaffAsync(LauncherFactory f, string role)
    {
        var client = Client(f);
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    internal static async Task<string> TokenAsync(HttpClient client)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/dashboard", Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? token, HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body };
        if (token is not null) request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request, Ct);
    }

    internal static async Task<StoredPicture> UploadAsync(HttpClient client, string token, byte[] bytes, string label = "beach.png", string url = "/api/splash/pictures")
    {
        using var r = await SendAsync(client, HttpMethod.Post, url + "?label=" + Uri.EscapeDataString(label), token, new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<StoredPicture>(Json, Ct))!;
    }

    private static SplashDesign PictureDesign(string name) => new(1280, 800, new SplashBackground("#223344", null, 50, 50, 0), false, 16,
    [
        new SplashBlock(1, SplashBlockKind.Picture, 40, 40, 400, 300, 1, "#000000", 100, null, false, 0,
            new SplashFont(24, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans),
            new SplashContent(Picture: new SplashPicture(name, SplashFit.Fill))),
    ]);

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string token, SplashDesign design, int basedOn) =>
        SendAsync(client, HttpMethod.Put, "/api/splash/draft?basedOn=" + basedOn, token, JsonContent.Create(design, options: Json));

    internal static string AuditText(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    private static string[] Stored(LauncherFactory f) =>
        Directory.Exists(f.Paths.SplashImages) ? Directory.GetFiles(f.Paths.SplashImages, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith("index.json", StringComparison.Ordinal)).ToArray() : [];

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task Owner_and_admin_can_upload_and_list(string role)
    {
        await using var f = Factory();
        var client = await StaffAsync(f, role);

        var picture = await UploadAsync(client, await TokenAsync(client), TestPictures.Png(40, 30));

        var list = JsonDocument.Parse(await client.GetStringAsync("/api/splash/pictures", Ct)).RootElement.GetProperty("pictures");
        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal(picture.Name, row.GetProperty("name").GetString());
        Assert.Equal("beach.png", row.GetProperty("label").GetString());
        Assert.False(row.GetProperty("inUse").GetBoolean());
        Assert.Equal(40, row.GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task Supervisor_and_anonymous_cannot_upload()
    {
        await using var f = Factory();
        var supervisor = await StaffAsync(f, "supervisor");

        using var bySupervisor = await SendAsync(supervisor, HttpMethod.Post, "/api/splash/pictures", null, new ByteArrayContent(TestPictures.Png(8, 8)));
        using var byAnyone = await SendAsync(Client(f), HttpMethod.Post, "/api/splash/pictures", null, new ByteArrayContent(TestPictures.Png(8, 8)));
        using var list = await supervisor.GetAsync("/api/splash/pictures", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, bySupervisor.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, byAnyone.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Empty(Stored(f));
    }

    [Fact]
    public async Task Upload_without_token_reads_nothing()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");

        using var r = await SendAsync(client, HttpMethod.Post, "/api/splash/pictures", null, new ByteArrayContent(TestPictures.Png(8, 8)));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(Stored(f));
    }

    [Fact]
    public async Task Wrong_type_answers_problem()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");

        using var r = await SendAsync(client, HttpMethod.Post, "/api/splash/pictures?label=x.png", await TokenAsync(client),
            new ByteArrayContent(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>")));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(PictureStore.NotAPicture, JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("problem").GetString());
        Assert.Empty(Stored(f));
    }

    [Fact]
    public async Task Api_oversize_upload_is_413()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");

        using var r = await SendAsync(client, HttpMethod.Post, "/api/splash/pictures", await TokenAsync(client),
            new ByteArrayContent(new byte[PictureStore.MaxUploadBytes + 1]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Empty(Stored(f));
    }

    [Fact]
    public async Task Picture_in_use_cannot_be_deleted()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var token = await TokenAsync(client);
        var picture = await UploadAsync(client, token, TestPictures.Png(8, 8));
        (await SaveAsync(client, token, PictureDesign(picture.Name), 0)).Dispose();

        using var refused = await SendAsync(client, HttpMethod.Delete, "/api/splash/pictures/" + picture.Name, token);
        (await SendAsync(client, HttpMethod.Post, "/api/splash/discard", token)).Dispose();
        using var deleted = await SendAsync(client, HttpMethod.Delete, "/api/splash/pictures/" + picture.Name, token);
        using var again = await SendAsync(client, HttpMethod.Delete, "/api/splash/pictures/" + picture.Name, token);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        var audit = AuditText(f);
        Assert.Contains("splash-image-uploaded", audit);
        Assert.Contains("splash-image-deleted", audit);
        Assert.Contains(picture.Name, audit);
    }

    [Fact]
    public async Task Draft_only_picture_is_not_public()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "owner");
        var token = await TokenAsync(client);
        var picture = await UploadAsync(client, token, TestPictures.Png(8, 8));
        (await SaveAsync(client, token, PictureDesign(picture.Name), 0)).Dispose();

        using var publicBefore = await Client(f).GetAsync("/welcome/images/" + picture.Name, Ct);
        using var staff = await client.GetAsync("/api/splash/pictures/" + picture.Name, Ct);
        (await SendAsync(client, HttpMethod.Post, "/api/splash/publish", token)).Dispose();
        using var publicAfter = await Client(f).GetAsync("/welcome/images/" + picture.Name, Ct);

        Assert.Equal(HttpStatusCode.NotFound, publicBefore.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staff.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publicAfter.StatusCode);
        Assert.Equal("image/png", publicAfter.Content.Headers.ContentType?.MediaType);
        Assert.True(publicAfter.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromSeconds(31536000), publicAfter.Headers.CacheControl?.MaxAge);
        Assert.Contains("immutable", publicAfter.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/welcome/images/..%2Fdraft.json")]
    [InlineData("/welcome/images/settings.json")]
    [InlineData("/welcome/images/index.json")]
    public async Task Public_route_refuses_bad_names(string url)
    {
        await using var f = Factory();

        using var r = await Client(f).GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
