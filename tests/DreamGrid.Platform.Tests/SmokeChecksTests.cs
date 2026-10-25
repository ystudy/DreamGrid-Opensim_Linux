using DreamGrid.Smoke;
using Xunit;

namespace DreamGrid.Platform.Tests;

public class SmokeChecksTests
{
    [Fact]
    public void LoginsEnabled_passes_on_the_RegionReady_line()
    {
        var r = SmokeChecks.LoginsEnabled("INITIALIZATION COMPLETE FOR Smoke Region - LOGINS ENABLED");
        Assert.True(r.Passed);
        Assert.Equal("C1", r.Id);
    }

    [Fact]
    public void LoginsEnabled_fails_on_empty_log()
    {
        Assert.False(SmokeChecks.LoginsEnabled("").Passed);
    }

    [Fact]
    public void NoForbiddenExceptions_fails_on_PlatformNotSupported()
    {
        var r = SmokeChecks.NoForbiddenExceptions(
            "System.PlatformNotSupportedException: BinaryFormatter serialization and deserialization have been removed.");
        Assert.False(r.Passed);
        Assert.Contains("PlatformNotSupportedException", r.Detail);
    }

    [Fact]
    public void NoForbiddenExceptions_fails_on_gdiplus()
    {
        Assert.False(SmokeChecks.NoForbiddenExceptions("Unable to load shared library 'libgdiplus' or one of its dependencies.").Passed);
    }

    [Fact]
    public void NoForbiddenExceptions_passes_on_clean_log()
    {
        Assert.True(SmokeChecks.NoForbiddenExceptions("[REGIONMODULES]: Loaded 42 modules").Passed);
    }

    [Fact]
    public void NoForbiddenExceptions_fails_on_Flotsam_BinaryFormatter_message()
    {
        // FlotsamAssetCache logs only e.Message, which never names the exception type.
        Assert.False(SmokeChecks.NoForbiddenExceptions(
            "[FLOTSAM ASSET CACHE]: Failed to get file for 1234: BinaryFormatter serialization and deserialization have been removed.").Passed);
    }

    [Fact]
    public void NoForbiddenExceptions_fails_when_the_Formatters_package_cannot_load()
    {
        Assert.False(SmokeChecks.NoForbiddenExceptions(
            "Could not load file or assembly 'System.Runtime.Serialization.Formatters, Version=10.0.0.0'").Passed);
    }

    [Fact]
    public void AssetCacheHasEntries_fails_when_only_empty_files_exist()
    {
        // Flotsam creates the temp file before serializing; a failed Serialize leaves it empty, silently.
        string dir = Directory.CreateTempSubdirectory("dg smoke cache ").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "ab", "cd"));
            File.WriteAllBytes(Path.Combine(dir, "ab", "cd", "tmp1234"), Array.Empty<byte>());
            Assert.False(SmokeChecks.AssetCacheHasEntries(dir).Passed);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AssetCacheHasEntries_fails_when_files_are_not_BinaryFormatter_streams()
    {
        string dir = Directory.CreateTempSubdirectory("dg smoke cache ").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "asset"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
            Assert.False(SmokeChecks.AssetCacheHasEntries(dir).Passed);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AssetCacheHasEntries_passes_on_a_real_BinaryFormatter_entry()
    {
        string dir = Directory.CreateTempSubdirectory("dg smoke cache ").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "ab", "cd"));
            File.WriteAllBytes(Path.Combine(dir, "ab", "cd", "asset"),
                new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x00 });
            File.WriteAllBytes(Path.Combine(dir, "ab", "cd", "asset.fac"), new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF });
            var r = SmokeChecks.AssetCacheHasEntries(dir);
            Assert.True(r.Passed);
            Assert.Equal("C4", r.Id);
            Assert.Contains("1 ", r.Detail); // the .fac file is not counted
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EstateReused_fails_when_the_second_boot_creates_an_estate()
    {
        string boot2 = "INITIALIZATION COMPLETE FOR Smoke Region - LOGINS ENABLED\n"
                     + "[ESTATE] Region Smoke Region is not part of an estate.\n"
                     + "[ESTATE]: No existing estates found.  You must create a new one.";
        var r = SmokeChecks.EstateReused(boot2);
        Assert.False(r.Passed);
        Assert.Equal("C6", r.Id);
    }

    [Fact]
    public void EstateReused_passes_on_a_plain_second_boot()
    {
        Assert.True(SmokeChecks.EstateReused("INITIALIZATION COMPLETE FOR Smoke Region - LOGINS ENABLED").Passed);
    }

    [Fact]
    public void EstateReused_fails_when_the_second_boot_never_enables_logins()
    {
        Assert.False(SmokeChecks.EstateReused("[SCENE]: Loading objects").Passed);
    }

    [Fact]
    public void DirectoryHasFiles_fails_for_empty_dir()
    {
        string dir = Directory.CreateTempSubdirectory("dg smoke empty ").FullName;
        try
        {
            Assert.False(SmokeChecks.DirectoryHasFiles("C4", dir).Passed);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DirectoryHasFiles_passes_with_one_file()
    {
        string dir = Directory.CreateTempSubdirectory("dg smoke one ").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "a", "b"));
            File.WriteAllText(Path.Combine(dir, "a", "b", "asset"), "x");
            var r = SmokeChecks.DirectoryHasFiles("C4", dir);
            Assert.True(r.Passed);
            Assert.Equal("C4", r.Id);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
