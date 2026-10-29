using DreamGrid.Launcher.Core.Config;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class HostAddressTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.20.1.1", false)]
    [InlineData("100.64.3.4", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("localhost", false)]
    [InlineData("LocalHost", false)]
    [InlineData("grid.localhost", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("203.0.113.7", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("mygrid.example.org", true)]
    [InlineData("2001:db8::1", true)]
    public void IsPublic(string? configured, bool expected) => Assert.Equal(expected, HostAddress.IsPublic(configured));
}
