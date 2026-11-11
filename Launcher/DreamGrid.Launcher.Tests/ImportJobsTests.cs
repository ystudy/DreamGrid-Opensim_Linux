using Xunit;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.ModelImport;
using Microsoft.Extensions.Time.Testing;

namespace DreamGrid.Launcher.Tests;

public sealed class ImportJobsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-importjobs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
    private LauncherPaths Paths => new(_root, Path.Combine(_root, "program"), null);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void Job_folder_name_is_random_hex()
    {
        var jobs = new ImportJobs(Paths, _time);
        var a = jobs.Create(Guid.NewGuid());
        var b = jobs.Create(Guid.NewGuid());
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), Path.GetFileName(a));
        Assert.NotEqual(a, b);
        Assert.Equal(Paths.Uploads, Path.GetDirectoryName(a));
        Assert.True(Directory.Exists(a));
    }

    [Fact]
    public void Jobs_of_a_guest_are_theirs_only()
    {
        var jobs = new ImportJobs(Paths, _time);
        var guest = Guid.NewGuid();
        var a = Path.GetFileName(jobs.Create(guest));
        var b = Path.GetFileName(jobs.Create(guest));
        jobs.Create(Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(Paths.Uploads, "not-a-job"));
        File.WriteAllText(Path.Combine(Paths.Uploads, "not-a-job", "owner"), guest.ToString("D"));
        Directory.CreateDirectory(Path.Combine(Paths.Uploads, new string('c', 32)));

        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), jobs.JobsOf(guest).Order(StringComparer.Ordinal));
        Assert.Empty(new ImportJobs(new LauncherPaths(Path.Combine(_root, "none"), _root, null), _time).JobsOf(guest));
    }

    [Fact]
    public void Only_one_slot()
    {
        var jobs = new ImportJobs(Paths, _time);
        Assert.True(jobs.TryEnterSlot());
        Assert.False(jobs.TryEnterSlot());
        jobs.LeaveSlot();
        Assert.True(jobs.TryEnterSlot());
    }

    [Fact]
    public void Sweep_deletes_jobs_older_than_an_hour()
    {
        var jobs = new ImportJobs(Paths, _time);
        var old = jobs.Create(Guid.NewGuid());
        _time.Advance(TimeSpan.FromMinutes(40));
        var recent = jobs.Create(Guid.NewGuid());
        _time.Advance(TimeSpan.FromMinutes(30));
        jobs.Sweep();
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void Owner_is_remembered_across_restarts_and_job_ids_are_checked()
    {
        var guest = Guid.NewGuid();
        var dir = new ImportJobs(Paths, _time).Create(guest);
        var id = Path.GetFileName(dir);
        var after = new ImportJobs(Paths, _time);
        Assert.True(after.Owns(guest, id));
        Assert.False(after.Owns(Guid.NewGuid(), id));
        Assert.False(after.Owns(guest, "..\\..\\x"));
        Assert.False(after.Owns(guest, new string('a', 32)));
        after.Delete("../escape");
        after.Delete(id);
        Assert.False(Directory.Exists(dir));
        Assert.False(after.Owns(guest, id));
    }
}
