using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// The owner's list of networks that may reach the staff website: each entry is a range (<c>192.168.1.0/24</c>, <c>2001:db8::/32</c>)
/// or a single address (<c>203.0.113.7</c>). An empty list allows every address. Loopback is always allowed, so the owner at the
/// grid computer can never be shut out. An IPv4 caller reported as an IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>, as a
/// dual-stack socket does) is compared as the IPv4 address. Bits after the prefix in a range are ignored.
/// </summary>
public static class NetworkList
{
    /// <summary>Null when every entry is a range or an address; otherwise a sentence about the first one that is not.</summary>
    public static string? Validate(IReadOnlyList<string> entries)
    {
        foreach (var entry in entries)
            if (!TryParse(entry, out _, out _))
                return $"\"{Shown(entry)}\" is not an address or a range. Use an address like 192.168.1.20 or a range like 192.168.1.0/24.";
        return null;
    }

    /// <summary>True when the list is null or empty, <paramref name="caller"/> is loopback, or an entry contains it.</summary>
    public static bool Allows(IReadOnlyList<string>? entries, IPAddress caller)
    {
        if (entries is not { Count: > 0 }) return true;
        caller = Unmapped(caller);
        if (IPAddress.IsLoopback(caller)) return true;
        var bytes = caller.GetAddressBytes();
        foreach (var entry in entries)
            if (TryParse(entry, out var network, out var prefix) && network.Length == bytes.Length && Matches(network, prefix, bytes))
                return true;
        return false;
    }

    private static IPAddress Unmapped(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private static string Shown(string entry) => entry.Length > 64 ? entry[..64] + "..." : entry;

    private static bool Matches(byte[] network, int prefix, byte[] caller)
    {
        var whole = prefix / 8;
        for (var i = 0; i < whole; i++)
            if (network[i] != caller[i]) return false;
        var rest = prefix % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (network[whole] & mask) == (caller[whole] & mask);
    }

    /// <summary>An address with an optional <c>/prefix</c>; a single address is its own range (/32 or /128).</summary>
    private static bool TryParse(string? entry, out byte[] network, out int prefix)
    {
        network = [];
        prefix = 0;
        var text = entry?.Trim() ?? "";
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return false;

        var slash = text.IndexOf('/');
        var addressText = slash < 0 ? text : text[..slash];
        if (addressText.Contains('%') || !IPAddress.TryParse(addressText, out var address)) return false;
        // IPAddress.TryParse also accepts "1" and "1.2" as IPv4; a range or address must be written out.
        if (address.AddressFamily == AddressFamily.InterNetwork && addressText.Count(c => c == '.') != 3) return false;

        var mapped = address.IsIPv4MappedToIPv6;
        var bytes = (mapped ? address.MapToIPv4() : address).GetAddressBytes();
        var bits = bytes.Length * 8;
        if (slash < 0)
        {
            prefix = bits;
        }
        else
        {
            var prefixText = text[(slash + 1)..];
            if (prefixText.Length is 0 or > 3 || !prefixText.All(char.IsAsciiDigit)
                || !int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out prefix)) return false;
            // "::ffff:10.0.0.0/104" is the IPv4 range 10.0.0.0/8.
            if (mapped) prefix -= 96;
            if (prefix < 0 || prefix > bits) return false;
        }
        network = bytes;
        return true;
    }
}
