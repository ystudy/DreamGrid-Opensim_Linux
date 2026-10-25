using System.Net;
using System.Text;

namespace DreamGrid.Launcher.Tests;

/// <summary>Records requests (with their form bodies) and answers with canned replies.</summary>
internal sealed class StubHandler(Func<RecordedRequest, (HttpStatusCode Status, string Body)> reply) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var rec = new RecordedRequest(request.Method, request.RequestUri!, body);
        lock (Requests) Requests.Add(rec);
        var (status, text) = reply(rec);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "text/xml") };
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body)
{
    public string Form(string key) =>
        Body.Split('&').Select(p => p.Split('=', 2))
            .Where(p => p[0] == key && p.Length == 2)
            .Select(p => Uri.UnescapeDataString(p[1].Replace('+', ' ')))
            .FirstOrDefault() ?? "";
}
