using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary><see cref="SimPort"/> is the UDP port of the region the login was sent to.</summary>
internal sealed record LoginResult(bool Ok, int? SimPort, string Message);

/// <summary>
/// A viewer's first step: the XML-RPC <c>login_to_simulator</c> call to Robust's login service. A successful answer
/// names the region (its address and UDP port) the viewer would connect to; no UDP is needed for the check.
/// </summary>
internal static class XmlRpcLogin
{
    /// <param name="start">The start location, e.g. <c>uri:Beach&amp;128&amp;128&amp;25</c>.</param>
    public static async Task<LoginResult> LoginAsync(HttpClient http, string loginUri, string first, string last, string password, string start,
        CancellationToken ct)
    {
        var passwd = "$1$" + Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password)));
        var members = new (string Name, string Value)[]
        {
            ("first", first), ("last", last), ("passwd", passwd), ("start", start),
            ("channel", "DreamGrid integration test"), ("version", "1.0.0"), ("platform", "Win"),
            ("mac", "00000000000000000000000000000000"), ("id0", "00000000000000000000000000000000"),
        };
        var request = new XElement("methodCall",
            new XElement("methodName", "login_to_simulator"),
            new XElement("params", new XElement("param", new XElement("value", new XElement("struct",
                members.Select(m => new XElement("member", new XElement("name", m.Name), new XElement("value", new XElement("string", m.Value)))))))));

        using var content = new StringContent(request.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
        using var response = await http.PostAsync(loginUri.TrimEnd('/') + "/", content, ct);
        var reply = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        // Only the top-level struct's members: nested ones (inventory folders, flags) repeat names.
        var top = reply.Root?.Element("params")?.Element("param")?.Element("value")?.Element("struct")?.Elements("member") ?? [];
        var answer = top
            .GroupBy(m => (string?)m.Element("name") ?? "")
            .ToDictionary(g => g.Key, g => g.First().Element("value")?.Value.Trim() ?? "");
        if (answer.Count == 0) return new LoginResult(false, null, "No login answer: " + reply.Root);
        var ok = answer.GetValueOrDefault("login") == "true";
        int? simPort = int.TryParse(answer.GetValueOrDefault("sim_port"), out var port) ? port : null;
        return new LoginResult(ok, simPort, answer.GetValueOrDefault("message") ?? answer.GetValueOrDefault("reason") ?? "");
    }
}
