using System.Text.RegularExpressions;
using GridGate.Launcher.Core;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-atomicfile-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string StateFile => Path.Combine(_dir, "state.json");

    [Fact]
    public void Shared_read_reads_while_another_handle_writes()
    {
        using var writer = new FileStream(StateFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("a"u8);
        writer.Flush();

        Assert.Equal("a", AtomicFile.ReadShared(StateFile));
    }

    [Fact]
    public void Shared_read_of_a_missing_file_throws() =>
        Assert.Throws<FileNotFoundException>(() => AtomicFile.ReadShared(StateFile));

    [Fact]
    public void Set_aside_moves_the_file_to_a_damaged_name()
    {
        File.WriteAllText(StateFile, "broken");

        var aside = AtomicFile.SetAside(StateFile);

        Assert.Matches(new Regex(@"state\.json\.damaged-\d{14}$"), aside);
        Assert.False(File.Exists(StateFile));
        Assert.Equal("broken", File.ReadAllText(aside));
    }

    [Fact]
    public void Set_aside_of_a_missing_file_throws() =>
        Assert.Throws<FileNotFoundException>(() => AtomicFile.SetAside(StateFile));

    [Fact]
    public void Write_creating_the_folder_makes_missing_folders()
    {
        var deep = Path.Combine(_dir, "a", "b", "state.json");

        AtomicFile.WriteAllTextCreatingFolder(deep, "x");

        Assert.Equal("x", File.ReadAllText(deep));
    }

    [Fact]
    public void Plain_write_still_needs_its_folder()
    {
        var deep = Path.Combine(_dir, "a", "b", "state.json");

        Assert.Throws<DirectoryNotFoundException>(() => AtomicFile.WriteAllText(deep, "x"));
    }
}
