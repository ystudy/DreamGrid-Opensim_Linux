using System.Text.Json;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Iar;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The grid-wide inventory backup settings, their checks, and the IAR folders (IAR plan task 1).</summary>
public sealed class IarSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-iars-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        var i = TestSettings.Create().Iar;
        Assert.False(i.DownloadsOn);
        Assert.False(i.UploadsOn);
        Assert.Equal(500, i.MaxUploadMb);
        Assert.Equal(4, i.UnpackFactor);
        Assert.Equal(20000, i.MaxUploadItems);
        Assert.Equal(2048, i.MaxDownloadMb);
        Assert.Equal(2, i.DownloadsPerDay);
        Assert.Equal(2, i.UploadsPerDay);
        Assert.Equal(1, i.JobsAtOnce);
        Assert.Equal(2048, i.DownloadKbPerSecond);
        Assert.Equal(24, i.LinkHours);
        Assert.Equal(5, i.MinFreeDiskGb);
    }

    [Fact]
    public void Old_settings_file_without_Iar_loads_defaults()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "settings.json");
        var store = new SettingsStore(file);
        store.Save(TestSettings.Create() with { Iar = new IarSettings(DownloadsOn: true) });
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var without = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Name != "iar") without[p.Name] = p.Value.Clone();
        Assert.Contains("iar", doc.RootElement.EnumerateObject().Select(p => p.Name));

        File.WriteAllText(file, JsonSerializer.Serialize(without));
        Assert.Equal(new IarSettings(), store.LoadOrCreate().Iar);

        without["iar"] = JsonDocument.Parse("null").RootElement.Clone();
        File.WriteAllText(file, JsonSerializer.Serialize(without));
        Assert.Equal(new IarSettings(), store.LoadOrCreate().Iar);
    }

    [Fact]
    public void Defaults_pass_the_validator() => Assert.True(SettingsValidator.Iar(new IarSettings()).Ok);

    public static TheoryData<string, IarSettings> OutOfRange => new()
    {
        { "MaxUploadMb", new IarSettings(MaxUploadMb: 0) },
        { "MaxUploadMb", new IarSettings(MaxUploadMb: 102401) },
        { "UnpackFactor", new IarSettings(UnpackFactor: 0) },
        { "UnpackFactor", new IarSettings(UnpackFactor: 21) },
        { "MaxUploadItems", new IarSettings(MaxUploadItems: 0) },
        { "MaxDownloadMb", new IarSettings(MaxDownloadMb: 0) },
        { "MaxDownloadMb", new IarSettings(MaxDownloadMb: 102401) },
        { "JobsAtOnce", new IarSettings(JobsAtOnce: 0) },
        { "JobsAtOnce", new IarSettings(JobsAtOnce: 9) },
        { "LinkHours", new IarSettings(LinkHours: 0) },
        { "LinkHours", new IarSettings(LinkHours: 169) },
        { "DownloadsPerDay", new IarSettings(DownloadsPerDay: -1) },
        { "DownloadsPerDay", new IarSettings(DownloadsPerDay: 101) },
        { "UploadsPerDay", new IarSettings(UploadsPerDay: -1) },
        { "UploadsPerDay", new IarSettings(UploadsPerDay: 101) },
        { "DownloadKbPerSecond", new IarSettings(DownloadKbPerSecond: 63) },
        { "MinFreeDiskGb", new IarSettings(MinFreeDiskGb: -1) },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void Validator_refuses_out_of_range(string field, IarSettings wanted)
    {
        var result = SettingsValidator.Iar(wanted);
        Assert.False(result.Ok);
        Assert.Single(result.Errors);
        Assert.True(result.Errors.ContainsKey(field));
    }

    [Fact]
    public void Iar_folders_are_owner_only_and_staging_is_cleared()
    {
        var paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        Directory.CreateDirectory(paths.IarsIncoming);
        Directory.CreateDirectory(paths.IarsSaving);
        Directory.CreateDirectory(paths.IarsReady);
        File.WriteAllBytes(Path.Combine(paths.IarsIncoming, "a.part"), [1]);
        File.WriteAllBytes(Path.Combine(paths.IarsSaving, "b.iar"), [1]);
        var ready = Path.Combine(paths.IarsReady, "c.iar");
        File.WriteAllBytes(ready, [1]);

        IarFolders.Prepare(paths);

        foreach (var dir in new[] { paths.Iars, paths.IarsIncoming, paths.IarsSaving, paths.IarsReady, paths.IarsStaff })
            Assert.True(Directory.Exists(dir), dir);
        Assert.Empty(Directory.GetFileSystemEntries(paths.IarsIncoming));
        Assert.Empty(Directory.GetFileSystemEntries(paths.IarsSaving));
        Assert.True(File.Exists(ready));
        Assert.StartsWith(paths.Data, paths.Iars, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(paths.Iars));
    }
}
