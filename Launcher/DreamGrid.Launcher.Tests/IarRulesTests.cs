using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Whether a resident may start a backup or an upload now (IAR plan task 7).</summary>
public sealed class IarRulesTests
{
    private const long Gb = 1L << 30;
    private static readonly IarSettings On = new(DownloadsOn: true, UploadsOn: true);

    private static IarRefusal? Ask(IarJobKind kind = IarJobKind.Save, IarSettings? s = null, bool personOn = true, bool named = false,
        int used = 0, bool hasJob = false, int running = 0, long free = 100 * Gb, bool readable = true) =>
        IarRules.Refusal(kind, s ?? On, personOn, named, used, hasJob, running, free, readable);

    [Fact]
    public void Allowed_when_all_pass()
    {
        Assert.Null(Ask(IarJobKind.Save));
        Assert.Null(Ask(IarJobKind.Load));
    }

    [Fact]
    public void Refusal_GridOff()
    {
        Assert.Equal(IarRefusal.GridOff, Ask(IarJobKind.Save, On with { DownloadsOn = false }));
        Assert.Equal(IarRefusal.GridOff, Ask(IarJobKind.Load, On with { UploadsOn = false }));
        Assert.Null(Ask(IarJobKind.Load, On with { DownloadsOn = false }));
    }

    [Fact] public void Refusal_PersonOff() => Assert.Equal(IarRefusal.PersonOff, Ask(personOn: false));
    [Fact] public void Refusal_NamedInCase() => Assert.Equal(IarRefusal.NamedInCase, Ask(named: true));

    [Fact]
    public void Refusal_DailyLimit()
    {
        Assert.Null(Ask(used: 1));
        Assert.Equal(IarRefusal.DailyLimit, Ask(used: 2));
        Assert.Equal(IarRefusal.DailyLimit, Ask(IarJobKind.Load, On with { UploadsPerDay = 0 }));
        Assert.Null(Ask(IarJobKind.Save, On with { UploadsPerDay = 0 }));
    }

    [Fact] public void Refusal_Busy() => Assert.Equal(IarRefusal.Busy, Ask(hasJob: true));

    [Fact]
    public void Refusal_GridBusy()
    {
        Assert.Equal(IarRefusal.GridBusy, Ask(running: 1));
        Assert.Null(Ask(s: On with { JobsAtOnce = 2 }, running: 1));
    }

    [Fact]
    public void Refusal_LowDisk()
    {
        Assert.Equal(IarRefusal.LowDisk, Ask(free: 5 * Gb - 1));
        Assert.Null(Ask(free: 5 * Gb));
    }

    [Fact]
    public void Refusal_TakedownsUnreadable_for_uploads_only()
    {
        Assert.Equal(IarRefusal.TakedownsUnreadable, Ask(IarJobKind.Load, readable: false));
        Assert.Null(Ask(IarJobKind.Save, readable: false));
    }

    [Fact]
    public void Order_grid_off_wins_over_daily_limit() =>
        Assert.Equal(IarRefusal.GridOff, Ask(s: On with { DownloadsOn = false }, personOn: false, named: true, used: 9, hasJob: true, running: 9, free: 0));

    [Fact]
    public void Copy_holder_is_not_named()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-td-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TakedownStore(root, new FakeTimeProvider(), NullLogger<TakedownStore>.Instance);
            var named = Guid.NewGuid();
            var holder = Guid.NewGuid();
            var notice = new TakedownNotice(TakedownReason.Rules, "Staff", null, null, null, new DateOnly(2026, 11, 1), "x");
            var c = store.Open(notice, false, n => TakedownStoreTests.Build(n, named) with
            {
                People = [new CasePerson(named, "N", PersonOnCase.Named), new CasePerson(holder, "H", PersonOnCase.CopyHolder)],
            });
            Assert.True(store.NamedInOpenCase(named));
            Assert.False(store.NamedInOpenCase(holder));

            store.Update(c.Number, x => x with { Open = false });
            Assert.False(store.NamedInOpenCase(named));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (DirectoryNotFoundException) { }
        }
    }
}
