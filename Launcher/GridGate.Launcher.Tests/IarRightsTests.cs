using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Each resident's inventory backup switches and own limits (IAR plan task 2).</summary>
public sealed class IarRightsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-iar-rights-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "iar-rights.json");
    private static readonly Guid Ann = Guid.Parse("0c000000-0000-0000-0000-00000000000c");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private IarRights Rights() => new(File);

    [Fact]
    public void Unknown_person_is_allowed_with_grid_limits()
    {
        var r = Rights().For(Ann);
        Assert.Equal(new IarPersonRights(Ann, false, false, null, null, null, null, null), r);
        Assert.Equal(new IarSettings(), IarRights.Effective(new IarSettings(), r));
    }

    [Fact]
    public void Override_replaces_only_its_own_value()
    {
        var grid = new IarSettings(UploadsPerDay: 2, DownloadsPerDay: 3);
        var r = new IarPersonRights(Ann, false, false, null, null, null, 10, null);
        Assert.Equal(grid with { UploadsPerDay = 10 }, IarRights.Effective(grid, r));

        var all = new IarPersonRights(Ann, true, true, 900, 4000, 7, 8, 512);
        Assert.Equal(grid with { MaxUploadMb = 900, MaxDownloadMb = 4000, DownloadsPerDay = 7, UploadsPerDay = 8, DownloadKbPerSecond = 512 },
            IarRights.Effective(grid, all));
    }

    [Fact]
    public void Set_is_kept_and_read_back_after_a_restart()
    {
        var wanted = new IarPersonRights(Ann, true, false, null, null, 5, null, null);
        Rights().Set(wanted);
        Assert.Equal(wanted, Rights().For(Ann));
    }

    [Fact]
    public void Setting_back_to_defaults_forgets_the_person()
    {
        var rights = Rights();
        rights.Set(new IarPersonRights(Ann, true, false, null, null, null, null, null));
        rights.Set(new IarPersonRights(Ann, false, false, null, null, null, null, null));
        Assert.DoesNotContain(Ann.ToString(), System.IO.File.ReadAllText(File), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Damaged_file_means_defaults_not_everyone_blocked()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ not json");
        Assert.Equal(new IarPersonRights(Ann, false, false, null, null, null, null, null), Rights().For(Ann));
        Assert.NotEmpty(Directory.GetFiles(_dir, "iar-rights.json.damaged-*"));
    }
}
