using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Guests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The avatars sign-up made that are still waiting for their email link (spec 2026-10-31-sign-up-design §5).</summary>
public sealed class PendingSignUpsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-pending-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "guests", "pending-signups.json");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Added_is_contained_until_removed()
    {
        var a = Guid.NewGuid();
        var store = new PendingSignUps(File_, _time);
        Assert.False(store.Contains(a));

        store.Add(a);
        Assert.True(store.Contains(a));
        Assert.False(store.Contains(Guid.NewGuid()));

        store.Remove(a);
        Assert.False(store.Contains(a));
    }

    [Fact]
    public void OlderThan_uses_the_clock()
    {
        var old = Guid.NewGuid();
        var young = Guid.NewGuid();
        var store = new PendingSignUps(File_, _time);
        store.Add(old);
        _time.Advance(TimeSpan.FromHours(20));
        store.Add(young);
        _time.Advance(TimeSpan.FromHours(5));

        Assert.Equal([old], store.OlderThan(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Survives_restart()
    {
        var a = Guid.NewGuid();
        new PendingSignUps(File_, _time).Add(a);

        Assert.True(new PendingSignUps(File_, _time).Contains(a));
    }

    [Fact]
    public void File_holds_no_email()
    {
        var a = Guid.NewGuid();
        new PendingSignUps(File_, _time).Add(a);

        var text = File.ReadAllText(File_);
        Assert.Contains(a.ToString(), text);
        Assert.DoesNotContain("@", text);
        // Only GUIDs, ISO times and JSON punctuation.
        var rest = Regex.Replace(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|\d{4}-\d\d-\d\dT[\d:.]+(Z|[+-]\d\d:\d\d)", "");
        Assert.Matches(@"^[\s{}\[\]"":,]*$", rest);
    }

    [Fact]
    public void A_damaged_file_reads_empty_and_is_set_aside()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "not json");
        var store = new PendingSignUps(File_, _time);
        Assert.Empty(store.OlderThan(TimeSpan.Zero));

        store.Add(Guid.NewGuid());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(File_)!, "pending-signups.json.damaged-*"));
    }
}
