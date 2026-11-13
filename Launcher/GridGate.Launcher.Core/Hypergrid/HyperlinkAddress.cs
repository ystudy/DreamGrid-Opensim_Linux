namespace GridGate.Launcher.Core.Hypergrid;

/// <summary>
/// A hypergrid link's address as staff type it (spec 2026-11-05-hypergrid-links-estates-design): the other grid's gatekeeper
/// (<see cref="Gatekeeper"/>, always <c>scheme://host:port/</c>) and the region there, or null for that grid's default region.
/// </summary>
public sealed record HyperlinkAddress(Uri Gatekeeper, string? Region)
{
    public const int MaxRegion = 64;

    /// <summary>What Robust's <c>link-region</c> is given and what the list shows: <c>http://host:port/</c>.</summary>
    public string Url => Gatekeeper.GetLeftPart(UriPartial.Authority) + "/";

    /// <summary>
    /// Reads <c>host:port</c>, <c>host:port:Region Name</c>, <c>http(s)://host:port/</c> or <c>http(s)://host:port/ Region Name</c>.
    /// Returns null and a plain-words <paramref name="problem"/> for anything else: another scheme, no host, a user name, a path,
    /// query or fragment, a bad port, or a region name that is too long or holds quotes or control characters.
    /// </summary>
    public static HyperlinkAddress? Parse(string? typed, out string? problem)
    {
        problem = null;
        var text = (typed ?? "").Trim();
        if (text.Length == 0)
        {
            problem = "Type the other grid's address, for example grid.example.org:8002.";
            return null;
        }
        if (text.Any(char.IsControl))
        {
            problem = "The address can't hold line breaks or tabs.";
            return null;
        }

        string address;
        string? region;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            var space = text.IndexOf(' ');
            (address, region) = space < 0 ? (text, null) : (text[..space], text[(space + 1)..]);
        }
        else
        {
            // host:port[:Region Name]; a region name may itself hold colons, so only the first two parts are the address.
            var parts = text.Split(':', 3);
            if (parts.Length < 2 || parts[0].Contains(' ') || parts[1].Contains(' '))
            {
                problem = "Type the address as host:port, for example grid.example.org:8002, or as http://grid.example.org:8002/.";
                return null;
            }
            address = $"http://{parts[0]}:{parts[1]}/";
            region = parts.Length == 3 ? parts[2] : null;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            problem = "Only http:// and https:// addresses can be linked.";
            return null;
        }
        if (uri.Host.Length == 0 || uri.HostNameType == UriHostNameType.Unknown)
        {
            problem = "The address needs a host name, for example grid.example.org.";
            return null;
        }
        if (uri.UserInfo.Length > 0)
        {
            problem = "The address can't hold a user name or password.";
            return null;
        }
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            problem = "Type only the grid's address and port, with no path after it.";
            return null;
        }

        region = region is null ? null : string.Join(' ', region.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (region is { Length: 0 }) region = null;
        if (region is not null)
        {
            if (region.Length > MaxRegion)
            {
                problem = $"A region name can be at most {MaxRegion} characters.";
                return null;
            }
            if (region.Contains('"'))
            {
                problem = "A region name can't hold double quotes.";
                return null;
            }
        }
        return new HyperlinkAddress(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), region);
    }

    /// <summary>Whether this address is the grid's own (by host and port, case-insensitive on the host).</summary>
    public bool IsSameGrid(string? ownBaseUrl, int ownPort, string? ownHostname) =>
        Gatekeeper.Port == ownPort
        && (SameHost(Gatekeeper.Host, ownHostname)
            || (Uri.TryCreate(ownBaseUrl, UriKind.Absolute, out var own) && SameHost(Gatekeeper.Host, own.Host)));

    private static bool SameHost(string a, string? b) => !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What Robust said to <c>link-region</c>: linked, refused (with its reason), or no answer in time (null).</summary>
public sealed record HyperlinkResult(bool? Linked, string? Reason);
