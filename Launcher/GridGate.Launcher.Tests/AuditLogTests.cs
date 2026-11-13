using System.Security.Claims;
using System.Text.Json;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class AuditLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-audit-" + Guid.NewGuid().ToString("N"));

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
    public void Write_for_a_signed_in_person_takes_name_id_and_role_from_their_claims()
    {
        var id = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.Name, "Ann Admin"), new(StaffSignIn.PrincipalClaim, id.ToString()),
             .. StaffSignIn.RoleClaims(StaffRole.Admin).Select(r => new Claim(ClaimTypes.Role, r))], "test"));
        var path = Path.Combine(_dir, "audit.log");
        using var log = new AuditLog(path, TimeProvider.System);

        log.Write(user, "10.0.0.5", "bots-settings", "max 3");
        log.Write(user, null, "email-test", null, "failed");

        var lines = Lines(path);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        var a = first.RootElement;
        Assert.Equal("Ann Admin", a.GetProperty("actor").GetString());
        Assert.Equal(id.ToString(), a.GetProperty("actorId").GetString());
        Assert.Equal("admin", a.GetProperty("role").GetString());
        Assert.Equal("10.0.0.5", a.GetProperty("ip").GetString());
        Assert.Equal("bots-settings", a.GetProperty("action").GetString());
        Assert.Equal("max 3", a.GetProperty("target").GetString());
        Assert.Equal("ok", a.GetProperty("result").GetString());
        using var second = JsonDocument.Parse(lines[1]);
        var b = second.RootElement;
        Assert.Equal(JsonValueKind.Null, b.GetProperty("ip").ValueKind);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("target").ValueKind);
        Assert.Equal("failed", b.GetProperty("result").GetString());
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

    [Fact]
    public void Recent_reads_newest_first_across_rolled_files()
    {
        var path = Path.Combine(_dir, "audit.log");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        // Small files, so the lines below roll over into audit.1.log and audit.2.log.
        using var log = new AuditLog(path, time, maxBytes: 400, files: 5);
        for (var i = 0; i < 12; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            log.Write(new AuditEntry("Test Admin", null, "admin", null, "suspended", i % 2 == 0 ? $"Gail Guest {i}" : "Other Person", "ok"));
        }
        Assert.True(File.Exists(Path.Combine(_dir, "audit.1.log")));

        var gail = log.Recent(e => e.Entry.Target?.StartsWith("Gail Guest", StringComparison.Ordinal) == true, 4);

        Assert.Equal(["Gail Guest 10", "Gail Guest 8", "Gail Guest 6", "Gail Guest 4"], gail.Select(e => e.Entry.Target));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 12, 11, 0, TimeSpan.Zero), gail[0].At);
        Assert.Equal("suspended", gail[0].Entry.Action);
    }

    [Fact]
    public void Recent_with_no_file_is_empty()
    {
        using var log = new AuditLog(Path.Combine(_dir, "none", "audit.log"), TimeProvider.System);
        Assert.Empty(log.Recent(_ => true, 20));
    }
}
