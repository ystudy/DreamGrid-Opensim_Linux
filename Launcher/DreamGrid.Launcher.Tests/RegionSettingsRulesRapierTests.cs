using System.Runtime.InteropServices;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class RegionSettingsRulesRapierTests
{
    [Fact]
    public void PhysicsEngines_include_Rapier()
    {
        Assert.Contains("Rapier", RegionSettingsRules.PhysicsEngines);
    }

    [Fact]
    public void Program_Rapier_RejectedOnUnsupportedPlatform()
    {
        var mac = RegionSettingsRules.Program("Rapier", new ProgramOptions(), 8, OSPlatform.OSX, Architecture.Arm64);
        Assert.Equal("Rapier isn't available on this computer.", mac.Errors["Physics"]);

        Assert.True(RegionSettingsRules.Program("Rapier", new ProgramOptions(), 8, OSPlatform.Linux, Architecture.X64).Ok);
    }

    [Theory]
    [InlineData("Linux", Architecture.X64, true)]
    [InlineData("Linux", Architecture.Arm64, true)]
    [InlineData("Windows", Architecture.X64, true)]
    [InlineData("Windows", Architecture.Arm64, false)]
    [InlineData("OSX", Architecture.X64, false)]
    [InlineData("OSX", Architecture.Arm64, false)]
    [InlineData("Linux", Architecture.X86, false)]
    public void RapierSupported_matches_the_shipped_libraries(string os, Architecture arch, bool expected)
    {
        Assert.Equal(expected, RegionSettingsRules.RapierSupported(OSPlatform.Create(os), arch));
    }

    [Fact]
    public void SettingLabels_Rapier_IsExperimental()
    {
        Assert.Equal("Rapier (experimental)", SettingLabels.Physics("Rapier"));
    }

    [Fact]
    public void Other_errors_message_names_Rapier()
    {
        var r = RegionSettingsRules.Program("Nope", new ProgramOptions(), 8, OSPlatform.Linux, Architecture.X64);
        Assert.Equal("Pick ubODE, BulletSim, BulletSimThreaded or Rapier, or use the grid default.", r.Errors["Physics"]);
    }
}
