using DreamGrid.Launcher.Core.Guests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Old names kept from others for a while after a rename (spec 2026-11-03-people-admin-design, Edit).</summary>
public sealed class HeldNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-held-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private string File => Path.Combine(_dir, "held-names.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Held_name_is_taken_for_30_days()
    {
        var held = new HeldNames(File, _time);
        Assert.True(held.Hold("Ann Baker", Guid.NewGuid()));
        _time.Advance(TimeSpan.FromDays(29));
        Assert.True(held.IsHeld("Ann Baker"));
        _time.Advance(TimeSpan.FromDays(2));
        Assert.False(held.IsHeld("Ann Baker"));
        Assert.Equal(TimeSpan.FromDays(30), HeldNames.HoldFor);
    }

    [Fact]
    public void Case_does_not_matter()
    {
        var held = new HeldNames(File, _time);
        held.Hold("Ann Baker", Guid.NewGuid());
        Assert.True(held.IsHeld("ann BAKER"));
        Assert.False(held.IsHeld("Ann Bakers"));
    }

    [Fact]
    public void Release_drops_the_owners_names()
    {
        var held = new HeldNames(File, _time);
        var ann = Guid.NewGuid();
        held.Hold("Ann Baker", ann);
        held.Hold("Bob Jones", Guid.NewGuid());
        Assert.True(held.Release(ann));
        Assert.False(held.IsHeld("Ann Baker"));
        Assert.True(held.IsHeld("Bob Jones"));
    }

    [Fact]
    public void Survives_a_restart()
    {
        new HeldNames(File, _time).Hold("Ann Baker", Guid.NewGuid());
        Assert.True(new HeldNames(File, _time).IsHeld("Ann Baker"));
    }
}
