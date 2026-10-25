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
