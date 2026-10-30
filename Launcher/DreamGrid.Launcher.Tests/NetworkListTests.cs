using System.Net;
using DreamGrid.Launcher.Core.Security;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class NetworkListTests
{
    private static bool Allows(string[]? entries, string caller) => NetworkList.Allows(entries, IPAddress.Parse(caller));

    [Fact]
    public void Empty_allows_all()
    {
        Assert.True(Allows(null, "203.0.113.9"));
        Assert.True(Allows([], "203.0.113.9"));
        Assert.True(Allows([], "2001:db8::1"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.5.5.5")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_always_allowed(string caller)
    {
        Assert.True(Allows(["10.0.0.0/8"], caller));
    }

    [Fact]
    public void Cidr_contains()
    {
        string[] list = ["192.168.1.0/24", "10.0.0.0/8"];
        Assert.True(Allows(list, "192.168.1.200"));
        Assert.True(Allows(list, "10.250.1.1"));
        Assert.False(Allows(list, "192.168.2.1"));
        Assert.False(Allows(list, "11.0.0.1"));
        Assert.True(Allows(["2001:db8::/32"], "2001:db8:ffff::5"));
        Assert.False(Allows(["2001:db8::/32"], "2001:db9::5"));
        Assert.False(Allows(["2001:db8::/32"], "10.0.0.1")); // another family never matches
        Assert.True(Allows(["0.0.0.0/0"], "203.0.113.9"));
        Assert.True(Allows(["192.168.1.77/24"], "192.168.1.5")); // host bits in the entry are ignored
    }

    [Fact]
    public void Single_address_entry()
    {
        Assert.True(Allows(["203.0.113.7"], "203.0.113.7"));
        Assert.False(Allows(["203.0.113.7"], "203.0.113.8"));
        Assert.True(Allows(["2001:db8::7"], "2001:db8::7"));
        Assert.False(Allows(["2001:db8::7"], "2001:db8::8"));
    }

    [Fact]
    public void Mapped_ipv6()
    {
        // Kestrel reports an IPv4 caller on a dual-stack socket as ::ffff:a.b.c.d.
        Assert.True(Allows(["192.168.1.0/24"], "::ffff:192.168.1.9"));
        Assert.False(Allows(["192.168.1.0/24"], "::ffff:192.168.2.9"));
        Assert.True(Allows(["203.0.113.7"], "::ffff:203.0.113.7"));
        Assert.True(Allows(["::ffff:203.0.113.7"], "203.0.113.7"));
    }

    [Fact]
    public void Validate_reports_bad_entry()
    {
        Assert.Null(NetworkList.Validate([]));
        Assert.Null(NetworkList.Validate(["10.0.0.0/8", "192.168.1.20", "2001:db8::/32", "fe80::1"]));

        var message = NetworkList.Validate(["10.0.0.0/8", "10.0.0.0/33", "nonsense"]);
        Assert.NotNull(message);
        Assert.Contains("10.0.0.0/33", message);
        Assert.DoesNotContain("nonsense", message); // the first bad entry only

        foreach (var bad in new[] { "nonsense", "10.0.0.0/", "10.0.0.0/-1", "10.0.0.0/x", "300.1.1.1", "2001:db8::/129", "/8", "", "10.0.0.0/8/8", "1.2.3.4 5.6.7.8" })
            Assert.NotNull(NetworkList.Validate([bad]));
    }
}
