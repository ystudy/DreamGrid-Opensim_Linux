using System.Net;
using System.Text.Json;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionsApiTests : IAsyncLifetime
{
    private static readonly Guid WelcomeId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid IsleId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private Func<Uri, HttpResponseMessage> _robust = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    private readonly List<Uri> _asked = new();
    private readonly LauncherFactory _f;

    public RegionsApiTests()
    {
        var welcome = new RegionSettings("Welcome", "Welcome", WelcomeId, UdpPort: 8004);
        var isle = new RegionSettings("Isle", "Isle", IsleId, X: 1010, Y: 1010, UdpPort: 8005, Parked: true);
        _f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true },
            Programs = [new ProgramSettings("Welcome", 8004, 8011, "ubODE"), new ProgramSettings("Isle", 8005, 8012, "ubODE")],
            Regions = [welcome, isle],
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddHttpClient("robust-map")
                .ConfigurePrimaryHttpMessageHandler(() => new RobustHandler(uri => { lock (_asked) _asked.Add(uri); return _robust(uri); })),
        };
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    private static HttpResponseMessage Reply(string type, byte[] body)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        r.Content.Headers.ContentType = new(type);
        return r;
    }

    [Fact]
    public async Task Tile_needs_sign_in()
    {
        using var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/api/map/tile/1/1000/1000", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Tile_passes_on_robust_jpeg()
    {
        _robust = _ => Reply("image/jpeg", Jpeg);
        using var client = await SignedInAsync();
        using var response = await client.GetAsync("/api/map/tile/1/1000/1000", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.CacheControl?.MaxAge);
        Assert.Equal(Jpeg, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("http://127.0.0.1:8002/map-1-1000-1000-objects.jpg", Assert.Single(_asked).ToString());
    }

    [Fact]
    public async Task Tile_is_404_when_robust_is_down()
    {
        _robust = _ => throw new HttpRequestException("refused");
        using var client = await SignedInAsync();
        using var response = await client.GetAsync("/api/map/tile/1/1000/1000", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Tile_is_404_when_robust_has_none()
    {
        _robust = _ => Reply("text/plain", []);
        using var client = await SignedInAsync();
        using var response = await client.GetAsync("/api/map/tile/1/1000/1000", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Tile_refuses_misaligned_spot()
    {
        using var client = await SignedInAsync();
        using var response = await client.GetAsync("/api/map/tile/2/1001/1000", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Tile_refuses_zoom_out_of_range()
    {
        using var client = await SignedInAsync();
        using var response = await client.GetAsync("/api/map/tile/9/1000/1000", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Regions_lists_every_region_with_state()
    {
        _f.Grid.Status = Status(RegionState.Running, RegionState.Parked);
        using var client = await SignedInAsync();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/regions", Ct));
        var regions = doc.RootElement.GetProperty("regions").EnumerateArray().ToList();
        Assert.Equal(2, regions.Count);
        Assert.Equal("Welcome", regions[0].GetProperty("name").GetString());
        Assert.Equal("running", regions[0].GetProperty("state").GetString());
        Assert.Equal("Running", regions[0].GetProperty("stateText").GetString());
        Assert.Equal("parked", regions[1].GetProperty("state").GetString());
        Assert.Equal("Parked", regions[1].GetProperty("stateText").GetString());
        Assert.Equal(1010, regions[1].GetProperty("x").GetInt32());
        Assert.False(regions[1].GetProperty("reserved").GetBoolean());
    }

    [Fact]
    public async Task Regions_shows_a_deleted_region_as_reserved()
    {
        _f.Grid.Status = Status(RegionState.Running, RegionState.Parked);
        _f.Grid.UpdateSettings(s => s with { Regions = [s.Regions[0], s.Regions[1] with { DeletedAt = DateTimeOffset.UtcNow }] });
        using var client = await SignedInAsync();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/regions", Ct));
        var isle = doc.RootElement.GetProperty("regions")[1];
        Assert.Equal("reserved", isle.GetProperty("state").GetString());
        Assert.Equal("Reserved", isle.GetProperty("stateText").GetString());
        Assert.True(isle.GetProperty("reserved").GetBoolean());
    }

    [Fact]
    public async Task Regions_needs_sign_in()
    {
        using var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/api/regions", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Placement_says_connected()
    {
        using var client = await SignedInAsync();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/regions/placement?x=1001&y=1000&size=1", Ct));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("This region will join Welcome on its east side.", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Placement_refuses_spot_of_parked_region()
    {
        using var client = await SignedInAsync();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/regions/placement?x=1010&y=1010&size=1", Ct));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("That spot is taken by Isle.", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Placement_ignores_the_named_region()
    {
        using var client = await SignedInAsync();
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/regions/placement?x=1010&y=1010&size=1&ignore={IsleId}", Ct));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Placement_refuses_spot_off_the_map()
    {
        using var client = await SignedInAsync();
        using var response = await client.GetAsync("/api/regions/placement?x=65535&y=1000&size=2", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("That spot is off the map.", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Placement_refuses_size_out_of_range()
    {
        using var client = await SignedInAsync();
        using var response = await client.GetAsync("/api/regions/placement?x=1000&y=1000&size=17", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static GridStatus Status(RegionState welcome, RegionState isle)
    {
        var up = new ComponentStatus("x", ProcessState.Running, null, null, null, null);
        return new GridStatus(up, up, [],
            [new RegionStatus(WelcomeId, "Welcome", "Welcome", welcome, true, 1000, 1000, 1, []),
             new RegionStatus(IsleId, "Isle", "Isle", isle, false, 1010, 1010, 1, [])],
            true, "http://localhost:8002");
    }

    private sealed class RobustHandler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request.RequestUri!));
    }
}
