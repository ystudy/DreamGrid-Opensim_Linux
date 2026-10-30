using System.Text.Json;
using DreamGrid.Launcher.Core.Logs;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class AuditLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Reads a file the log still has open for writing.</summary>
    private static string[] Lines(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s);
        return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Line_is_json_with_utc_time()
    {
        var path = Path.Combine(_dir, "audit.log");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 34, 56, TimeSpan.FromHours(-7)));
        var id = Guid.NewGuid();
        using var log = new AuditLog(path, time);

        log.Write(new AuditEntry("Test Owner", id, "owner", "10.0.0.5", "sign-in", null, "ok"));
        log.Write(new AuditEntry("Machine\nInjected \"x\"", null, "machine", null, "command", "StopRobust", "ACK"));

        var lines = Lines(path);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        var root = first.RootElement;
        Assert.Equal("2026-10-30T19:34:56.000Z", root.GetProperty("time").GetString());
        Assert.EndsWith("Z", root.GetProperty("time").GetString());
        Assert.Equal("Test Owner", root.GetProperty("actor").GetString());
        Assert.Equal(id.ToString(), root.GetProperty("actorId").GetString());
        Assert.Equal("owner", root.GetProperty("role").GetString());
        Assert.Equal("10.0.0.5", root.GetProperty("ip").GetString());
        Assert.Equal("sign-in", root.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("target").ValueKind);
        Assert.Equal("ok", root.GetProperty("result").GetString());

        // A control character in a field cannot start a second line; it stays inside the string.
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("Machine\nInjected \"x\"", second.RootElement.GetProperty("actor").GetString());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("actorId").ValueKind);
        Assert.Equal("StopRobust", second.RootElement.GetProperty("target").GetString());
    }

    [Fact]
    public void Rotates_at_max_bytes()
    {
        var path = Path.Combine(_dir, "audit.log");
        using var log = new AuditLog(path, new FakeTimeProvider(), maxBytes: 400, files: 3);

        for (var i = 0; i < 30; i++)
            log.Write(new AuditEntry("Test Owner", null, "admin", null, "request", $"POST /page{i}", "200"));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_dir, "audit.1.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "audit.2.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "audit.3.log"))); // three files in all
        Assert.Contains("page29", string.Join('\n', Lines(path)));
        Assert.All(Lines(path), l => JsonDocument.Parse(l).Dispose());
    }

    [Fact]
    public void Concurrent_writes_stay_one_object_per_line()
    {
        var path = Path.Combine(_dir, "audit.log");
        using var log = new AuditLog(path, TimeProvider.System);

        Parallel.For(0, 200, i => log.Write(new AuditEntry("Test Owner", null, "admin", null, "request", "POST /x" + i, "200")));

        var lines = Lines(path);
        Assert.Equal(200, lines.Length);
        Assert.All(lines, l => JsonDocument.Parse(l).Dispose());
    }
}
