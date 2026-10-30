using System.Collections.Concurrent;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.Logging;
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
    public void A_damaged_file_remembers_nothing_and_is_moved_aside_before_the_next_write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "{ not json");
        var logs = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        var log = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(logs))).CreateLogger<SuspendedStore>();
        var a = Guid.NewGuid();
        var store = new SuspendedStore(File_, log);

        Assert.Null(store.PreviousLevel(a));
        store.Remember(a, 70);

        Assert.Equal(70, new SuspendedStore(File_).PreviousLevel(a));
        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(File_)!, "suspended.json.damaged-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
        Assert.Contains(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("suspended.json"));
    }

    [Fact]
    public void A_file_that_cannot_be_read_throws_and_is_never_written_over()
    {
        // Something in the way of the file (here a folder) is an error to report, not an empty list: the next write would
        // otherwise forget every remembered level.
        Directory.CreateDirectory(File_);
        var a = Guid.NewGuid();
        var store = new SuspendedStore(File_);

        Assert.True(Record.Exception(() => store.PreviousLevel(a)) is IOException or UnauthorizedAccessException);
        Assert.True(Record.Exception(() => store.Remember(a, 70)) is IOException or UnauthorizedAccessException);
        Assert.True(Record.Exception(() => store.Forget(a)) is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(File_));
    }

    [Fact]
    public void A_missing_folder_remembers_nothing()
    {
        Assert.Null(new SuspendedStore(Path.Combine(_dir, "nowhere", "suspended.json")).PreviousLevel(Guid.NewGuid()));
    }
}
