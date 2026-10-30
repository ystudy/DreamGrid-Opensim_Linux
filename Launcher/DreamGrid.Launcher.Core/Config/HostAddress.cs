using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Config;

public static class HostAddress
{
    /// <summary>The configured hostname, trimmed, when it is non-empty and valid; otherwise the detected address.</summary>
    public static string Effective(string? configured)
    {
        var trimmed = configured?.Trim();
        return !string.IsNullOrEmpty(trimmed) && SettingsValidator.HostnameError(trimmed) is null ? trimmed : Detect();
    }

    /// <summary>
    /// Whether a configured hostname is reachable from the internet: false for null or empty (the detected LAN address) and for
    /// loopback, unspecified, broadcast, private, link-local and CGNAT addresses and for "localhost" and names under it;
    /// true for any other IP and for any other name.
    /// </summary>
    public static bool IsPublic(string? configured)
    {
        var host = configured?.Trim();
        if (string.IsNullOrEmpty(host)) return false;
        if (!IPAddress.TryParse(host, out var ip))
            return !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip) || IPAddress.Broadcast.Equals(ip))
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC); // fe80::/10, fc00::/7
        var b = ip.GetAddressBytes();
        return !(b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }

    /// <summary>The LAN IPv4 of the default route; 127.0.0.1 when none can be found. Sends no packet.</summary>
    public static string Detect()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 53);
            if (socket.LocalEndPoint is IPEndPoint { Address: var ip } && !IPAddress.Any.Equals(ip))
                return ip.ToString();
        }
        catch (Exception e) when (e is SocketException or InvalidOperationException or PlatformNotSupportedException) { }
        return "127.0.0.1";
    }
}
