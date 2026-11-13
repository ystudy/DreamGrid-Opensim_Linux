using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GridGate.Launcher.Core.Hypergrid;

/// <summary>What the link test found: whether the other grid answered, the name it gave, and what to tell staff.</summary>
public sealed record ProbeResult(bool Ok, string? GridName, string Message);

/// <summary>The link test for a hypergrid link (spec 2026-11-05-hypergrid-links-estates-design, [S] checks).</summary>
public interface IGridProbe
{
    /// <summary>Fetches <c>&lt;gatekeeper&gt;/get_grid_info</c> safely. Never throws for anything the other grid does.</summary>
    Task<ProbeResult> CheckAsync(Uri gatekeeper, CancellationToken ct);
}

/// <summary>
/// <see cref="IGridProbe"/> over its own connection rules: a 10-second limit, no redirects followed, at most 64 KB read, and a
/// connection only to a public address. The address is checked on every address the host name resolves to, at the moment of
/// connecting, so a DNS answer that changes between a check and the request can't point it at this computer or its network.
/// </summary>
public sealed class GridProbe : IGridProbe, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public const int MaxBytes = 64 * 1024;

    private readonly HttpClient _http;

    /// <param name="allowAddress">Which addresses may be connected to; <see cref="IsPublic"/> outside tests.</param>
    public GridProbe(Func<IPAddress, bool>? allowAddress = null)
    {
        var allow = allowAddress ?? IsPublic;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
                var allowed = addresses.Where(allow).ToList();
                if (allowed.Count == 0) throw new BlockedAddressException(host);
                // Every address is checked: a name that also resolves to a private one is refused, so it can't be raced.
                if (allowed.Count != addresses.Length) throw new BlockedAddressException(host);
                Exception? last = null;
                foreach (var ip in allowed)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException e)
                    {
                        socket.Dispose();
                        last = e;
                    }
                }
                throw last ?? new SocketException((int)SocketError.HostNotFound);
            },
        };
        _http = new HttpClient(handler) { Timeout = Timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GridGate-HypergridCheck/1");
    }

    public async Task<ProbeResult> CheckAsync(Uri gatekeeper, CancellationToken ct)
    {
        var where = gatekeeper.GetLeftPart(UriPartial.Authority);
        try
        {
            using var response = await _http.GetAsync(new Uri(gatekeeper, "/get_grid_info"), HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
                return new ProbeResult(false, null, $"{where} answered with a redirect. Links aren't followed to another address; type the address it points to.");
            if (!response.IsSuccessStatusCode)
                return new ProbeResult(false, null, $"{where} answered, but not as a grid ({(int)response.StatusCode}). Check the address and port.");
            var body = await ReadLimitedAsync(response, ct);
            if (body is null) return new ProbeResult(false, null, $"{where} sent more than {MaxBytes / 1024} KB; that isn't a grid's information.");
            if (GridName(body) is not { } name)
                return new ProbeResult(false, null, $"{where} answered, but not with a grid's information. Check the address and port.");
            return new ProbeResult(true, name, $"{name} answered at {where}.");
        }
        catch (Exception e) when (Blocked(e) is { } host)
        {
            return new ProbeResult(false, null, $"{host} is on a private network or this computer. Only grids on the internet can be linked.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return new ProbeResult(false, null, $"Nothing answered at {where} within {Timeout.TotalSeconds:0} seconds. Check the address, and that the other grid is running.");
        }
    }

    /// <summary>The grid's name from get_grid_info XML, or null when it isn't grid info.</summary>
    public static string? GridName(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var root = XDocument.Load(reader).Root;
            if (root?.Name.LocalName != "gridinfo") return null;
            var name = root.Element("gridname")?.Value.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            name = new string(name.Where(c => !char.IsControl(c)).ToArray());
            return name.Length > 64 ? name[..64] : name;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a link test may connect to <paramref name="ip"/>: not loopback, unspecified, broadcast, multicast or reserved, and not
    /// private (10/8, 172.16/12, 192.168/16, CGNAT 100.64/10, IPv6 ULA fc00::/7) or link-local (169.254/16 with the cloud metadata
    /// address, fe80::/10). IPv4 mapped into IPv6 is checked as IPv4.
    /// </summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 198 && b[1] is 18 or 19)
                || b[0] >= 224);
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
        var v6 = ip.GetAddressBytes();
        if ((v6[0] & 0xFE) == 0xFC) return false; // fc00::/7
        // 64:ff9b::/96 (NAT64) and ::/96 (IPv4-compatible) carry an IPv4 address: judge that one.
        if (v6[..12].All(x => x == 0) || (v6[0] == 0 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b && v6[4..12].All(x => x == 0)))
            return IsPublic(new IPAddress(v6[12..]));
        return true;
    }

    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        return read > MaxBytes ? null : Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string? Blocked(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is BlockedAddressException b) return b.Host;
        return null;
    }

    public void Dispose() => _http.Dispose();

    private sealed class BlockedAddressException(string host) : IOException($"{host} is not a public address.")
    {
        public string Host { get; } = host;
    }
}
