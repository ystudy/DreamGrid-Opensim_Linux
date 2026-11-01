using System.Net;

namespace DreamGrid.Launcher.Core.Config;

/// <summary>Finds the address the internet sees this computer at.</summary>
public interface IPublicAddressFinder
{
    /// <summary>This computer's public IPv4 address; null when no lookup service answers with one.</summary>
    Task<string?> FindAsync(CancellationToken ct);
}

/// <summary>
/// Asks a few "what is my IP" services in turn and takes the first answer that is a public IP address. Each answers with
/// the bare address as text. Only asked when the owner presses the button, never in the background.
/// </summary>
public sealed class PublicAddressFinder(HttpClient http) : IPublicAddressFinder
{
    private static readonly Uri[] Services =
    [
        new("https://api.ipify.org"),
        new("https://checkip.amazonaws.com"),
        new("https://ipv4.icanhazip.com"),
    ];

    public async Task<string?> FindAsync(CancellationToken ct)
    {
        foreach (var service in Services)
        {
            try
            {
                var text = (await http.GetStringAsync(service, ct)).Trim();
                if (IPAddress.TryParse(text, out var ip) && HostAddress.IsPublic(text)) return ip.ToString();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
        }
        return null;
    }
}
