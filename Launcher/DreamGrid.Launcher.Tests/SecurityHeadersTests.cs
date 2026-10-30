using System.Net.Http.Headers;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class SecurityHeadersTests : IAsyncLifetime
{
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly LauncherFactory _factory = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static void AssertHeaders(HttpResponseMessage resp)
    {
        Assert.Equal(Csp, Single(resp, "Content-Security-Policy"));
        Assert.Equal("nosniff", Single(resp, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Single(resp, "Referrer-Policy"));
        Assert.Equal("DENY", Single(resp, "X-Frame-Options"));
    }

    private static string Single(HttpResponseMessage resp, string name)
    {
        Assert.True(resp.Headers.TryGetValues(name, out var values) || resp.Content.Headers.TryGetValues(name, out values), $"{name} is missing");
        return Assert.Single(values!);
    }

    [Fact]
    public async Task Headers_on_page()
    {
        using var resp = await _factory.CreateClient().GetAsync("/welcome", Ct);

        AssertHeaders(resp);
    }

    [Fact]
    public async Task Headers_on_api()
    {
        // Not signed in: the answer is a 401, and it carries the headers too.
        using var resp = await _factory.CreateClient().GetAsync("/api/status", Ct);

        AssertHeaders(resp);
    }

    [Fact]
    public async Task Headers_on_static_file()
    {
        using var resp = await _factory.CreateClient().GetAsync("/css/site.css", Ct);

        Assert.True(resp.IsSuccessStatusCode);
        AssertHeaders(resp);
    }

    [Fact]
    public async Task Headers_on_legacy_text_reply()
    {
        using var resp = await _factory.CreateClient().GetAsync("/?type=Booted&UUID=6f1f0000-0000-0000-0000-00000000aaaa", Ct);

        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
        AssertHeaders(resp);
    }
}
