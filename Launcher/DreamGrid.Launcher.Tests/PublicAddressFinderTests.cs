using System.Net;
using DreamGrid.Launcher.Core.Config;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class PublicAddressFinderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers each service by its host: the text, or no answer when the host isn't listed.</summary>
    private sealed class Answers(Dictionary<string, string> byHost) : HttpMessageHandler
    {
        public List<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            Asked.Add(host);
            return byHost.TryGetValue(host, out var text)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) })
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("no answer"));
        }
    }

    [Fact]
    public async Task Takes_the_first_service_answer_trimmed()
    {
        var answers = new Answers(new() { ["api.ipify.org"] = "203.0.113.7\n" });

        Assert.Equal("203.0.113.7", await new PublicAddressFinder(new HttpClient(answers)).FindAsync(Ct));
        Assert.Equal(["api.ipify.org"], answers.Asked);
    }

    [Fact]
    public async Task Asks_the_next_service_when_one_does_not_answer_or_answers_junk()
    {
        var answers = new Answers(new() { ["checkip.amazonaws.com"] = "<html>busy</html>", ["ipv4.icanhazip.com"] = "198.51.100.4" });

        Assert.Equal("198.51.100.4", await new PublicAddressFinder(new HttpClient(answers)).FindAsync(Ct));
        Assert.Equal(3, answers.Asked.Count);
    }

    [Fact]
    public async Task A_network_address_is_not_an_answer()
    {
        var answers = new Answers(new() { ["api.ipify.org"] = "192.168.1.20", ["checkip.amazonaws.com"] = "10.0.0.5", ["ipv4.icanhazip.com"] = "100.64.0.9" });

        Assert.Null(await new PublicAddressFinder(new HttpClient(answers)).FindAsync(Ct));
    }
}
