using DreamGrid.Launcher.Security;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The levels remembered for suspended people, so unsuspending gives each back theirs.</summary>
public sealed class SuspendedStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-suspended-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "security", "suspended.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void A_remembered_level_survives_a_restart_and_is_forgotten_on_request()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var store = new SuspendedStore(File_);
        Assert.Null(store.PreviousLevel(a));

        store.Remember(a, 50);
        store.Remember(b, 0);

        var again = new SuspendedStore(File_);
        Assert.Equal(50, again.PreviousLevel(a));
        Assert.Equal(0, again.PreviousLevel(b));

        again.Forget(a);
        Assert.Null(new SuspendedStore(File_).PreviousLevel(a));
        Assert.Equal(0, new SuspendedStore(File_).PreviousLevel(b));
    }

    [Fact]
    public void Remembering_again_replaces_the_level()
    {
        var a = Guid.NewGuid();
        var store = new SuspendedStore(File_);
        store.Remember(a, 50);
        store.Remember(a, 150);
        Assert.Equal(150, new SuspendedStore(File_).PreviousLevel(a));
    }

    [Fact]
    public void A_damaged_file_remembers_nothing_and_is_replaced_on_the_next_write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "{ not json");
        var a = Guid.NewGuid();
        var store = new SuspendedStore(File_);

        Assert.Null(store.PreviousLevel(a));
        store.Remember(a, 70);

        Assert.Equal(70, new SuspendedStore(File_).PreviousLevel(a));
    }
}
