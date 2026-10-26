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
