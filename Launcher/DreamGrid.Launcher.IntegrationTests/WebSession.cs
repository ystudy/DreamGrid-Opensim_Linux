using System.Net;
using AngleSharp.Html.Parser;
using Xunit;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>A page reached after following every redirect.</summary>
internal sealed record WebPage(Uri Uri, string Html)
{
    public string Path => Uri.AbsolutePath;
    public string PathAndQuery => Uri.PathAndQuery;
}

/// <summary>
/// A browser stand-in: keeps cookies, follows redirects itself (so each hop's cookies are kept), and submits a
/// page's form with the fields the page really has, including its antiforgery token.
/// </summary>
internal sealed class WebSession : IDisposable
{
    private const int MaxRedirects = 10;
    private readonly HttpClient _client;
    private readonly HtmlParser _parser = new();

    public WebSession(int port)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };
        // Step 2 starts the database and Robust within the request when they are not running yet.
        _client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes(5) };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<string> GetStringAsync(string url)
    {
        using var response = await _client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>A plain GET that does not insist on 200: the status and content type.</summary>
    public async Task<(HttpStatusCode Status, string? ContentType)> GetStatusAsync(string url)
    {
        using var response = await _client.GetAsync(url, Ct);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType);
    }

    public async Task<WebPage> FollowAsync(string url) => await FollowAsync(await _client.GetAsync(url, Ct));

    /// <summary>
    /// Posts the page's form (<paramref name="selector"/>, by default the one in &lt;main&gt;) with its own
    /// values, overridden by <paramref name="fields"/>; every field must exist in the form.
    /// </summary>
    public async Task<WebPage> SubmitAsync(WebPage page, Dictionary<string, string> fields, string selector = "main form")
    {
        var doc = await _parser.ParseDocumentAsync(page.Html, Ct);
        var form = doc.QuerySelector(selector);
        Assert.True(form is not null, $"{page.Path} has no form matching \"{selector}\"");
        Assert.Equal("post", form!.GetAttribute("method"), StringComparer.OrdinalIgnoreCase);

        var values = new List<KeyValuePair<string, string>>();
        var names = new HashSet<string>();
        foreach (var input in form.QuerySelectorAll("input[name], select[name], textarea[name]"))
        {
            var name = input.GetAttribute("name")!;
            names.Add(name);
            if (fields.ContainsKey(name)) continue;
            var type = input.GetAttribute("type")?.ToLowerInvariant();
            if (type is "radio" or "checkbox" && !input.HasAttribute("checked")) continue;
            values.Add(new(name, input.GetAttribute("value") ?? ""));
        }
        Assert.Contains("__RequestVerificationToken", names);
        foreach (var (name, value) in fields)
        {
            Assert.True(names.Contains(name), $"the form on {page.Path} has no field \"{name}\"");
            values.Add(new(name, value));
        }

        var action = new Uri(page.Uri, form.GetAttribute("action") is { Length: > 0 } a ? a : page.PathAndQuery);
        using var content = new FormUrlEncodedContent(values);
        var response = await _client.PostAsync(action, content, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect,
            $"POST {action.PathAndQuery} answered {(int)response.StatusCode}, not a redirect:{Environment.NewLine}{ErrorText(await response.Content.ReadAsStringAsync(Ct))}");
        return await FollowAsync(response);
    }

    private async Task<WebPage> FollowAsync(HttpResponseMessage first)
    {
        var response = first;
        try
        {
            for (var hop = 0; ; hop++)
            {
                var uri = response.RequestMessage!.RequestUri!;
                if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.SeeOther))
                {
                    Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {uri.PathAndQuery} answered {(int)response.StatusCode}");
                    return new WebPage(uri, await response.Content.ReadAsStringAsync(Ct));
                }
                Assert.True(hop < MaxRedirects, $"more than {MaxRedirects} redirects, the last to {response.Headers.Location}");
                var next = new Uri(uri, response.Headers.Location!);
                response.Dispose();
                response = await _client.GetAsync(next, Ct);
            }
        }
        finally { response.Dispose(); }
    }

    /// <summary>A page's error boxes and field errors, so a refused form says why.</summary>
    private string ErrorText(string html)
    {
        var doc = _parser.ParseDocument(html);
        var errors = doc.QuerySelectorAll(".error-box, .field-error").Select(e => e.TextContent.Trim()).Where(t => t != "");
        return string.Join(Environment.NewLine, errors);
    }

    public void Dispose() => _client.Dispose();
}
