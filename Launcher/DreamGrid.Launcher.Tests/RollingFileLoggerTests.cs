using Microsoft.Extensions.Logging;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Write_after_dispose_does_not_recreate_file()
    {
        var path = Path.Combine(_dir, "launcher.log");
        var provider = new RollingFileLoggerProvider(path);
        var logger = provider.CreateLogger("t");
        logger.LogInformation("first");
        Assert.True(File.Exists(path));

        provider.Dispose();
        File.Delete(path);
        logger.LogInformation("late");

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Failed_roll_keeps_appending()
    {
        var path = Path.Combine(_dir, "launcher.log");
        // With 3 files, launcher.2.log is the oldest; a directory there cannot be deleted.
        Directory.CreateDirectory(Path.Combine(_dir, "launcher.2.log"));
        File.WriteAllText(Path.Combine(_dir, "launcher.2.log", "keep"), "x");
        using var provider = new RollingFileLoggerProvider(path, maxBytes: 200, files: 3);
        var logger = provider.CreateLogger("t");

        for (var i = 0; i < 30; i++) logger.LogInformation("line {I} {Pad}", i, new string('x', 30));
        logger.LogInformation("the last line");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var text = new StreamReader(stream).ReadToEnd();
        Assert.Contains("line 29", text);
        Assert.Contains("the last line", text);
    }
}
