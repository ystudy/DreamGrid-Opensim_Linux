using System.Collections.Concurrent;
using DreamGrid.Launcher.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class JsonStateFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-jsonstate-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _logs = new();

    public JsonStateFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string StateFile => Path.Combine(_dir, "state.json");

    private JsonStateFile<Dictionary<string, int>> New(string? path = null) =>
        new(path ?? StateFile, () => [], new CapturingLoggerProvider(_logs).CreateLogger("test"));

    [Fact]
    public void Missing_file_gives_empty()
    {
        var state = New().Load();

        Assert.Empty(state);
        Assert.Empty(_logs);
    }

    [Fact]
    public void Damaged_file_is_set_aside_and_warned()
    {
        File.WriteAllText(StateFile, "{ this is not json");

        var state = New().Load();

        Assert.Empty(state);
        Assert.False(File.Exists(StateFile));
        var aside = Assert.Single(Directory.GetFiles(_dir, "state.json.damaged-*"));
        Assert.Matches(@"state\.json\.damaged-\d{14}$", aside);
        Assert.Equal("{ this is not json", File.ReadAllText(aside));
        var warning = Assert.Single(_logs, l => l.Level == LogLevel.Warning);
        Assert.Contains(StateFile, warning.Message);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var file = New();

        Assert.True(file.Save(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }));

        var again = New().Load();
        Assert.Equal(2, again.Count);
        Assert.Equal(1, again["a"]);
        Assert.Equal(2, again["b"]);
    }

    [Fact]
    public void A_failed_save_returns_false_and_is_logged_once_until_a_save_succeeds()
    {
        // A directory where the file should be makes every save fail.
        Directory.CreateDirectory(StateFile);
        var file = New();

        Assert.False(file.Save([]));
        Assert.False(file.Save([]));
        Assert.Single(_logs, l => l.Level == LogLevel.Warning);

        Directory.Delete(StateFile);
        Assert.True(file.Save([]));

        File.Delete(StateFile);
        Directory.CreateDirectory(StateFile);
        Assert.False(file.Save([]));
        Assert.Equal(2, _logs.Count(l => l.Level == LogLevel.Warning));
    }

    [Fact]
    public void Save_creates_a_missing_folder()
    {
        var nested = Path.Combine(_dir, "a", "b", "state.json");

        Assert.True(New(nested).Save(new Dictionary<string, int> { ["x"] = 7 }));

        Assert.Equal(7, New(nested).Load()["x"]);
    }
}
