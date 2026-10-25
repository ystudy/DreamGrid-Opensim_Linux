using DreamGrid.Launcher.Core.OpenSim;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class RegionAlertTests
{
    private const string Rid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Fact]
    public void RegionAlert_parses_region_ready_and_regionpid_payloads()
    {
        var ready = RegionAlert.Parse("{\"alert\":\"region_ready\",\"login\":\"enabled\",\"region_name\":\"Welcome\",\"region_id\":\"" + Rid + "\"}");
        var pid = RegionAlert.Parse("{\"alert\":\"regionpid\",\"login\":\"regionpid\",\"region_name\":\"Welcome\",\"region_id\":\"" + Rid + "\",\"pid\":1234}");

        Assert.Equal(new RegionAlert("region_ready", "enabled", "Welcome", Guid.Parse(Rid), null), ready);
        Assert.Equal(new RegionAlert("regionpid", "regionpid", "Welcome", Guid.Parse(Rid), 1234), pid);
    }

    [Fact]
    public void Parse_accepts_disabled_login()
    {
        var a = RegionAlert.Parse("{\"alert\":\"region_ready\",\"login\":\"disabled\",\"region_name\":\"W\",\"region_id\":\"" + Rid + "\"}");

        Assert.Equal("disabled", a!.Login);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"login\":\"enabled\"}")]
    public void Parse_returns_null_for_garbage(string json) => Assert.Null(RegionAlert.Parse(json));
}
