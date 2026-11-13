using GridGate.Launcher.Guests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class UploadRightsTests : IDisposable
{
    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-upload-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string File_ => Path.Combine(_dir, "guests", "upload-rights.json");

    private UploadRights New() => new(File_, _time);

    [Fact]
    public void Damaged_file_means_nobody()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "{ this is not json");

        var rights = New();

        Assert.False(rights.Has(Ann));
        Assert.False(File.Exists(File_));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(File_)!, "upload-rights.json.damaged-*"));
    }

    [Fact]
    public void Missing_file_means_nobody() => Assert.False(New().Has(Ann));

    [Fact]
    public void Grant_then_remove()
    {
        var rights = New();

        rights.Grant(Ann, "Ann Guest");
        rights.Grant(Ann, "Ann Guest"); // twice is still one entry

        Assert.True(rights.Has(Ann));
        Assert.False(rights.Has(Bob));
        Assert.True(New().Has(Ann)); // kept in the file

        rights.Remove(Bob); // nobody to remove
        rights.Remove(Ann);

        Assert.False(rights.Has(Ann));
        Assert.False(New().Has(Ann));
    }
}
