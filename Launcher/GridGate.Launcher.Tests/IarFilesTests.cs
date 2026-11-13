using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Iar;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Residents' finished backups and their download links (IAR plan task 7).</summary>
public sealed class IarFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gg-iarfiles-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly LauncherPaths _paths;
    private static readonly Guid Ann = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    public IarFilesTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        IarFolders.Prepare(_paths);
        Directory.CreateDirectory(_paths.Guests);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private IarFiles Files() => new(_paths, _time);

    private string Staged(int bytes = 10)
    {
        var path = Path.Combine(_paths.IarsSaving, Guid.NewGuid().ToString("N") + ".iar");
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Publish_moves_the_file_and_makes_a_long_random_token()
    {
        var staged = Staged(42);
        var left = new List<LeftOutItem> { new("dress", "Shop", LeftOutReason.NoTransfer) };
        var r = Files().Publish(Ann, "stamp-1", staged, Day, left, 7);

        Assert.False(File.Exists(staged));
        Assert.True(File.Exists(Files().PathOf(r)));
        Assert.StartsWith(_paths.IarsReady, Files().PathOf(r), StringComparison.Ordinal);
        Assert.Equal((Ann, 7, 42L, _time.GetUtcNow() + Day), (r.Owner, r.Items, r.Bytes, r.Expires));
        Assert.Equal(left, r.LeftOut);
        Assert.True(r.Token.Length >= 43);
        Assert.DoesNotContain(Ann.ToString(), r.File, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(r.Token, Files().Find(Ann)!.Token); // kept across a restart
    }

    [Fact]
    public void Publish_replaces_previous_file()
    {
        var files = Files();
        var first = files.Publish(Ann, "s", Staged(), Day, [], 1);
        var second = files.Publish(Ann, "s", Staged(), Day, [], 2);
        Assert.False(File.Exists(files.PathOf(first)));
        Assert.Equal(second, files.Find(Ann));
        Assert.Null(files.ForDownload(first.Token, Ann, "s"));
    }

    [Fact]
    public void ForDownload_refuses_other_principal_wrong_stamp_expired_and_unknown_token()
    {
        var files = Files();
        var r = files.Publish(Ann, "stamp-1", Staged(), Day, [], 1);

        Assert.Equal(r, files.ForDownload(r.Token, Ann, "stamp-1"));
        Assert.Null(files.ForDownload(r.Token, Bob, "stamp-1"));
        Assert.Null(files.ForDownload(r.Token, Ann, "stamp-2"));
        Assert.Null(files.ForDownload("not-a-token", Ann, "stamp-1"));
        Assert.Null(files.ForDownload("", Ann, "stamp-1"));
        _time.Advance(Day + TimeSpan.FromSeconds(1));
        Assert.Null(files.ForDownload(r.Token, Ann, "stamp-1"));
        Assert.Null(files.Find(Ann));
    }

    [Fact]
    public void Sweep_removes_expired_and_keeps_staff_files()
    {
        var files = Files();
        var old = files.Publish(Ann, "s", Staged(), TimeSpan.FromHours(1), [], 1);
        var fresh = files.Publish(Bob, "s", Staged(), Day, [], 1);
        var stray = Path.Combine(_paths.IarsReady, "stray.iar");
        File.WriteAllBytes(stray, [1]);
        var staff = Path.Combine(_paths.IarsStaff, "evidence.iar");
        File.WriteAllBytes(staff, [1]);
        _time.Advance(TimeSpan.FromHours(2));

        files.Sweep();

        Assert.False(File.Exists(files.PathOf(old)));
        Assert.True(File.Exists(files.PathOf(fresh)));
        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(staff));
        Assert.Null(Files().Find(Ann));
        Assert.Equal(fresh.Token, Files().Find(Bob)!.Token);
    }

    [Fact]
    public void Find_is_null_when_the_file_is_gone()
    {
        var files = Files();
        var r = files.Publish(Ann, "s", Staged(), Day, [], 1);
        File.Delete(files.PathOf(r));
        Assert.Null(files.Find(Ann));
    }
}
