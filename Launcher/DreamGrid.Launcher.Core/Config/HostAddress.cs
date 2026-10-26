using System.Net;
using System.Net.Sockets;

namespace DreamGrid.Launcher.Core.Config;

public static class HostAddress
{
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
        catch (SocketException) { }
        return "127.0.0.1";
    }
}
