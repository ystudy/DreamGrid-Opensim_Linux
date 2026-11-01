using DreamGrid.Launcher.Core.Guests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The email changes waiting for their link (spec 2026-10-31-resident-account-design §6).</summary>
public sealed class PendingEmailChangesTests : IDisposable
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-email-changes-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "guests", "email-changes.json");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Set_replaces_earlier()
    {
        var a = Guid.NewGuid();
        var store = new PendingEmailChanges(File_, _time);
        Assert.Null(store.Get(a, "tok", Day));
        store.Set(a, "first@example.com", "tok");
        store.Set(a, "second@example.com", "tok");
        Assert.Equal("second@example.com", store.Get(a, "tok", Day));
        store.Remove(a);
        Assert.Null(store.Get(a, "tok", Day));
    }

    [Fact]
    public void Get_ignores_older_than_max_age()
    {
        var a = Guid.NewGuid();
        var store = new PendingEmailChanges(File_, _time);
        store.Set(a, "new@example.com", "tok");
        _time.Advance(TimeSpan.FromHours(23));
        Assert.Equal("new@example.com", store.Get(a, "tok", Day));
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Null(store.Get(a, "tok", Day));
    }

    [Fact]
    public void Survives_restart()
    {
        var a = Guid.NewGuid();
        new PendingEmailChanges(File_, _time).Set(a, "new@example.com", "tok");
        Assert.Equal("new@example.com", new PendingEmailChanges(File_, _time).Get(a, "tok", Day));
    }

    [Fact]
    public void Prune_on_set_drops_old()
    {
        var old = Guid.NewGuid();
        var store = new PendingEmailChanges(File_, _time);
        store.Set(old, "old@example.com", "tok");
        _time.Advance(TimeSpan.FromHours(25));
        store.Set(Guid.NewGuid(), "young@example.com", "tok");
        Assert.DoesNotContain("old@example.com", File.ReadAllText(File_));
        Assert.Contains("young@example.com", File.ReadAllText(File_));
    }

    [Fact]
    public void Get_needs_the_token_it_was_set_with()
    {
        var a = Guid.NewGuid();
        var store = new PendingEmailChanges(File_, _time);
        store.Set(a, "new@example.com", "token-one");
        Assert.Null(store.Get(a, "token-two", Day));
        Assert.Null(store.Get(a, null, Day));
        Assert.Equal("new@example.com", store.Get(a, "token-one", Day));
        Assert.DoesNotContain("token-one", File.ReadAllText(File_));
    }
}
