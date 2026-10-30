using System.Net;
using System.Net.Sockets;

namespace DreamGrid.Launcher.Tests;

/// <summary>Free ports for a test's settings.</summary>
internal static class TestPorts
{
    /// <summary>
    /// <paramref name="count"/> distinct ports that are free on TCP and on UDP: the grid's port check binds a region's
    /// UDP port too, and on Windows a port free on TCP can be held on UDP by another program.
    /// </summary>
    public static int[] Free(int count)
    {
        var held = new List<IDisposable>();
        var ports = new List<int>();
        try
        {
            while (ports.Count < count)
            {
                var tcp = new TcpListener(IPAddress.Any, 0);
                tcp.Start();
                held.Add(new Stopper(tcp));
                var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                try
                {
                    held.Add(new UdpClient(new IPEndPoint(IPAddress.Any, port)));
                    ports.Add(port);
                }
                catch (SocketException) { } // taken on UDP: keep it held so it is not handed out again, and try another
            }
            return [.. ports];
        }
        finally
        {
            foreach (var h in held) h.Dispose();
        }
    }

    private sealed class Stopper(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }
}
