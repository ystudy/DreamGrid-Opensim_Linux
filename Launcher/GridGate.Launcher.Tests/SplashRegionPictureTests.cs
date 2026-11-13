using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static GridGate.Launcher.Tests.SplashPictureApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>A region's map tile copied in as a splash picture (spec §4.2, Places to go), and the region list the editor picks from.</summary>
public sealed class SplashRegionPictureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Sandbox = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>Robust's map service: answers <see cref="Tile"/> for every tile and records what was asked.</summary>
    private sealed class FakeRobustMap : HttpMessageHandler
    {
        public ConcurrentQueue<string> Asked { get; } = new();
        public Func<HttpResponseMessage> Tile { get; set; } = () => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestPictures.Jpeg(256, 256)) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } },
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Enqueue(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Tile());
        }
    }

    private static LauncherFactory WithMap(FakeRobustMap map) =>
        Factory(s => s.AddHttpClient(RegionsApi.RobustMapClient).ConfigurePrimaryHttpMessageHandler(() => map));

    [Fact]
    public async Task Region_tile_becomes_a_picture()
    {
        var map = new FakeRobustMap();
        await using var f = WithMap(map);
        f.Regions.Result = new GridRegionList([new(Sandbox, "Sandbox", 1000, 1002)], true);
        var client = await StaffAsync(f, "admin");

        using var r = await SendAsync(client, HttpMethod.Post, $"/api/splash/pictures/from-region/{Sandbox}", await TokenAsync(client));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("Map of Sandbox", json.GetProperty("label").GetString());
        Assert.EndsWith(".jpg", json.GetProperty("name").GetString());
        Assert.Contains("/map-1-1000-1002-objects.jpg", map.Asked);
        Assert.Contains("splash-image-uploaded", AuditText(f));
    }

    [Fact]
    public async Task Unknown_region_is_404()
    {
        await using var f = WithMap(new FakeRobustMap());
        var client = await StaffAsync(f, "admin");

        using var r = await SendAsync(client, HttpMethod.Post, $"/api/splash/pictures/from-region/{Guid.NewGuid()}", await TokenAsync(client));

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Missing_tile_is_404()
    {
        // Robust answers a missing tile with a 200, text/plain and an empty body.
        var map = new FakeRobustMap { Tile = () => new(HttpStatusCode.OK) { Content = new StringContent("") } };
        await using var f = WithMap(map);
        f.Regions.Result = new GridRegionList([new(Sandbox, "Sandbox", 1000, 1002)], true);
        var client = await StaffAsync(f, "admin");

        using var r = await SendAsync(client, HttpMethod.Post, $"/api/splash/pictures/from-region/{Sandbox}", await TokenAsync(client));

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("problem", await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Region_list_unavailable_is_503()
    {
        await using var f = WithMap(new FakeRobustMap());
        f.Regions.Result = GridRegionList.Unavailable;
        var client = await StaffAsync(f, "admin");

        using var r = await SendAsync(client, HttpMethod.Post, $"/api/splash/pictures/from-region/{Sandbox}", await TokenAsync(client));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact]
    public async Task Without_token_nothing_is_fetched()
    {
        var map = new FakeRobustMap();
        await using var f = WithMap(map);
        f.Regions.Result = new GridRegionList([new(Sandbox, "Sandbox", 1000, 1002)], true);
        var client = await StaffAsync(f, "admin");

        using var r = await SendAsync(client, HttpMethod.Post, $"/api/splash/pictures/from-region/{Sandbox}", null);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(map.Asked);
    }

    [Fact]
    public async Task Splash_api_lists_regions()
    {
        await using var f = WithMap(new FakeRobustMap());
        f.Regions.Result = new GridRegionList([new(Sandbox, "Sandbox", 1000, 1002)], true);
        var client = await StaffAsync(f, "owner");

        var regions = JsonDocument.Parse(await client.GetStringAsync("/api/splash", Ct)).RootElement.GetProperty("regions");

        var row = Assert.Single(regions.EnumerateArray());
        Assert.Equal(Sandbox, row.GetProperty("id").GetGuid());
        Assert.Equal("Sandbox", row.GetProperty("name").GetString());
    }
}
