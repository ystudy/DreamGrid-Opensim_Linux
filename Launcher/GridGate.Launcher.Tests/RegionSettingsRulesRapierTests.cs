using System.Runtime.InteropServices;
using System.Xml.Linq;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

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
    [InlineData("Linux", Architecture.Arm64, false)] // until bin/lib64/librapier-arm64.so ships
    [InlineData("Windows", Architecture.X64, true)]
    [InlineData("Windows", Architecture.Arm64, false)]
    [InlineData("OSX", Architecture.X64, false)]
    [InlineData("OSX", Architecture.Arm64, false)]
    [InlineData("Linux", Architecture.X86, false)]
    public void RapierSupported_matches_the_shipped_libraries(string os, Architecture arch, bool expected)
    {
        Assert.Equal(expected, RegionSettingsRules.RapierSupported(OSPlatform.Create(os), arch));
    }

    /// <summary>
    /// Every platform RapierSupported accepts must have a library: the BulletS dllmap line for "rapier" that matches it
    /// must name a file that is in bin/lib64.
    /// </summary>
    [Fact]
    public void Every_supported_platform_has_its_dllmap_target_in_bin()
    {
        var bin = RepoBin();
        var maps = XDocument.Load(Path.Combine(bin, "OpenSim.Region.PhysicsModule.BulletS.dll.config"))
            .Descendants("dllmap").Where(m => (string?)m.Attribute("dll") == "rapier").ToList();
        var systems = new (OSPlatform Os, string Name)[]
        {
            (OSPlatform.Linux, "linux"), (OSPlatform.Windows, "windows"), (OSPlatform.OSX, "osx"), (OSPlatform.FreeBSD, "freebsd"),
        };
        var checkedAny = false;
        foreach (var (os, osName) in systems)
        {
            foreach (var arch in Enum.GetValues<Architecture>())
            {
                if (!RegionSettingsRules.RapierSupported(os, arch)) continue;
                checkedAny = true;
                var cpu = DllmapCpu(arch);
                var map = maps.FirstOrDefault(m => Matches((string?)m.Attribute("os"), osName) && Matches((string?)m.Attribute("cpu"), cpu));
                Assert.True(map is not null, $"No rapier dllmap line for {osName} {cpu}");
                var target = (string)map!.Attribute("target")!;
                Assert.True(File.Exists(Path.Combine(bin, target)), $"{osName} {cpu}: dllmap target bin/{target} is missing");
            }
        }
        Assert.True(checkedAny);
    }

    private static string DllmapCpu(Architecture arch) => arch switch
    {
        Architecture.X64 => "x86-64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => arch.ToString().ToLowerInvariant(),
    };

    /// <summary>Mono dllmap matching: no attribute matches everything; "!a,b" matches anything but a and b.</summary>
    private static bool Matches(string? attribute, string value)
    {
        if (string.IsNullOrEmpty(attribute)) return true;
        var negate = attribute.StartsWith('!');
        var names = attribute.TrimStart('!').Split(',', StringSplitOptions.TrimEntries);
        return names.Contains(value) != negate;
    }

    private static string RepoBin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("GridGate.sln not found"), "bin");
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
