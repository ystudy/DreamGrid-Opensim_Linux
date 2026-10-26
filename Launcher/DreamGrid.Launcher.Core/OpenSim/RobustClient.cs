using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.OpenSim;

public sealed record RobustAccount(Guid PrincipalId, string FirstName, string LastName, int UserLevel, string Email);

public interface IRobustClient
{
    Task<bool> IsReadyAsync(CancellationToken ct);
    Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct);
    Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct);
}

/// <summary>
/// Robust's public grid-info endpoint plus the private account and auth services. The ports are read from the
/// settings on every call, so a changed Robust port applies at once.
/// </summary>
public sealed class RobustClient(HttpClient http, Func<LauncherSettings> settings) : IRobustClient
{
    private Uri PrivateBase => new($"http://127.0.0.1:{settings().RobustPrivatePort}");
    private Uri PublicBase => new($"http://127.0.0.1:{settings().RobustPublicPort}");

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(new Uri(PublicBase, "/get_grid_info"), ct);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return body.Contains("<gridinfo>", StringComparison.OrdinalIgnoreCase)
                || body.Contains("gridname", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    public async Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct)
    {
        var root = await PostAsync("/accounts", ct,
            ("METHOD", "getaccount"), ("FirstName", first), ("LastName", last));
        var result = root?.Element("result");
        if (result is null || !result.HasElements) return null; // "null" or absent

        string Field(string name) => result.Element(name)?.Value.Trim() ?? "";
        if (!Guid.TryParse(Field("PrincipalID"), out var id)) return null;
        _ = int.TryParse(Field("UserLevel"), out var level);
        return new RobustAccount(id, Field("FirstName"), Field("LastName"), level, Field("Email"));
    }

    public async Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct)
    {
        var md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(plainPassword)));
        var root = await PostAsync("/auth/plain", ct,
            ("METHOD", "authenticate"), ("PRINCIPAL", principal.ToString()), ("PASSWORD", md5));
        return !string.IsNullOrWhiteSpace(root?.Element("Token")?.Value);
    }

    private async Task<XElement?> PostAsync(string path, CancellationToken ct, params (string Key, string Value)[] fields)
    {
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        using var resp = await http.PostAsync(new Uri(PrivateBase, path), content, ct);
        resp.EnsureSuccessStatusCode();
        try { return XDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).Root; }
        catch (System.Xml.XmlException) { return null; }
    }
}
