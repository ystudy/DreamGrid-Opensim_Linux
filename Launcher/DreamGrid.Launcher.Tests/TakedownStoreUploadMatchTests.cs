using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>A taken-down item that comes back in an upload is noted on its case, closed cases too (IAR plan task 5).</summary>
public sealed class TakedownStoreUploadMatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-takedowns-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Ann = Guid.Parse("0c000000-0000-0000-0000-00000000000c");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    private TakedownStore Store() => new(_root, _time, NullLogger<TakedownStore>.Instance);

    private static readonly TakedownNotice Notice = new(TakedownReason.Copyright, "Rita", null, null, null, new DateOnly(2026, 11, 1), "ours");

    private static TakedownCase Closed(string n) => TakedownStoreTests.Build(n, Ann) with
    {
        Open = false,
        Events = [new CaseEvent(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), "Test Admin", null, "closed", "done")],
    };

    [Fact]
    public void Upload_match_on_closed_case_adds_event_and_stays_closed()
    {
        var store = Store();
        var c = store.Open(Notice, true, Closed);
        Assert.False(c.MatchedSinceClosed);

        var after = store.AppendUploadMatch(c.Number, Ann, "Ann Lee", 2, _time.GetUtcNow());

        Assert.NotNull(after);
        Assert.False(after.Open);
        var e = after.Events[^1];
        Assert.Equal(("upload-match", "GridGate", (Guid?)null), (e.What, e.Staff, e.StaffId));
        Assert.Equal("2 items came back in an upload from Ann Lee and were removed.", e.Why);
        Assert.True(after.MatchedSinceClosed);
        Assert.True(Store().Get(c.Number)!.MatchedSinceClosed);
    }

    [Fact]
    public void Upload_match_on_open_case_is_noted_too()
    {
        var store = Store();
        var c = store.Open(Notice, true, n => TakedownStoreTests.Build(n, Ann));
        var after = store.AppendUploadMatch(c.Number, Ann, "Ann Lee", 1, _time.GetUtcNow())!;
        Assert.True(after.Open);
        Assert.Equal("1 item came back in an upload from Ann Lee and was removed.", after.Events[^1].Why);
        Assert.False(after.MatchedSinceClosed);
    }

    [Fact]
    public void Unknown_case_changes_nothing() => Assert.Null(Store().AppendUploadMatch("TD-2026-999", Ann, "Ann", 1, _time.GetUtcNow()));

    [Fact]
    public async Task Case_list_shows_match_since_closed()
    {
        await using var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        { Before = TestAuth.Apply };
        TestAuth.AddStaff(f.Robust);
        var store = f.Services.GetRequiredService<TakedownStore>();
        var c = store.Open(Notice, true, Closed);
        store.AppendUploadMatch(c.Number, Ann, "Ann Lee", 1, DateTimeOffset.UtcNow);

        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        using var r = await client.GetAsync("/__test/signin?role=admin", Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/people/takedowns", Ct));
        Assert.Contains("Closed (match since closed)", page.QuerySelector("main table")!.TextContent);
    }
}
