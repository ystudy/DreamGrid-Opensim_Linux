using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Iar;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Each resident's backups and uploads over the last 24 hours (IAR plan task 7).</summary>
public sealed class IarCountsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gg-iarcount-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private static readonly Guid Ann = Guid.NewGuid();
    private string File => Path.Combine(_dir, "iar-counts.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Counts_roll_after_24_hours()
    {
        var counts = new IarCounts(File, _time);
        counts.Use(Ann, IarJobKind.Save);
        _time.Advance(TimeSpan.FromHours(23));
        counts.Use(Ann, IarJobKind.Save);
        counts.Use(Ann, IarJobKind.Load);
        Assert.Equal(2, counts.UsedToday(Ann, IarJobKind.Save));
        Assert.Equal(1, counts.UsedToday(Ann, IarJobKind.Load));
        Assert.Equal(0, counts.UsedToday(Guid.NewGuid(), IarJobKind.Save));

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, counts.UsedToday(Ann, IarJobKind.Save));
        Assert.Equal(1, new IarCounts(File, _time).UsedToday(Ann, IarJobKind.Save)); // kept across a restart
    }

    [Fact]
    public void GiveBack_restores_one()
    {
        var counts = new IarCounts(File, _time);
        counts.Use(Ann, IarJobKind.Load);
        counts.Use(Ann, IarJobKind.Load);
        counts.GiveBack(Ann, IarJobKind.Load);
        Assert.Equal(1, counts.UsedToday(Ann, IarJobKind.Load));
        counts.GiveBack(Ann, IarJobKind.Load);
        counts.GiveBack(Ann, IarJobKind.Load);
        Assert.Equal(0, counts.UsedToday(Ann, IarJobKind.Load));
    }

    [Fact]
    public void Principals_are_not_stored_in_plain_text()
    {
        new IarCounts(File, _time).Use(Ann, IarJobKind.Save);
        Assert.DoesNotContain(Ann.ToString(), System.IO.File.ReadAllText(File), StringComparison.OrdinalIgnoreCase);
    }
}
