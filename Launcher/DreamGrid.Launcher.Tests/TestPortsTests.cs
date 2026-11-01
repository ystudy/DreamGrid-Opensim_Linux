using System.Net;
using System.Net.Sockets;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The test port picker: a "first port" setting is counted up from, so a port near the top would leave nowhere to go.</summary>
public sealed class TestPortsTests
{
    [Fact]
    public void A_port_near_the_top_is_passed_over()
    {
        var high = Enumerable.Range(TestPorts.Highest + 1, 65535 - TestPorts.Highest).Reverse().First(Bindable);
        var first = true;
        var ports = TestPorts.Free(2, () =>
        {
            if (!first) return new TcpListener(IPAddress.Any, 0);
            first = false;
            return new TcpListener(IPAddress.Any, high);
        });
        Assert.DoesNotContain(high, ports);
        Assert.All(ports, p => Assert.InRange(p, 1, TestPorts.Highest));
    }

    private static bool Bindable(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }
}
