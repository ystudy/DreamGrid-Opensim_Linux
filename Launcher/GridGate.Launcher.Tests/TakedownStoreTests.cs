using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Takedown cases on disk (spec 2026-11-04-inventory-takedown-design, Evidence and The case record).</summary>
public sealed class TakedownStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-takedowns-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    private TakedownStore Store() => new(_root, _time, NullLogger<TakedownStore>.Instance);

    private static readonly TakedownNotice Notice = new(TakedownReason.Copyright, "Rita Rights", "Rights Ltd", "rita@example.com", "R-1",
        new DateOnly(2026, 11, 3), "The sword is ours.");

    internal static TakedownCase Build(string number, params Guid[] people) =>
        new(number, true, Notice, true, [.. people.Select(p => new CasePerson(p, "Some One", PersonOnCase.Named))], [], [], null);

    [Fact]
    public void First_case_of_the_year_is_001()
    {
        var store = Store();
        Assert.Equal("TD-2026-001", store.Open(Notice, true, n => Build(n)).Number);
        Assert.Equal("TD-2026-002", store.Open(Notice, true, n => Build(n)).Number);
        _time.SetUtcNow(new DateTimeOffset(2027, 1, 1, 0, 0, 1, TimeSpan.Zero));
        Assert.Equal("TD-2027-001", store.Open(Notice, true, n => Build(n)).Number);
    }

    [Fact]
    public void Two_cases_opened_at_once_get_different_numbers()
    {
        var store = Store();
        var numbers = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 20, _ => numbers.Add(store.Open(Notice, true, n => Build(n)).Number));
        Assert.Equal(20, numbers.Distinct().Count());
        Assert.Equal(20, Directory.GetDirectories(_root, "TD-*").Length);
        Assert.Equal(20, Store().All().Count);
    }

    [Fact]
    public void Cases_survive_a_restart()
    {
        var store = Store();
        var ann = Guid.NewGuid();
        var row = new InventoryItemRow("a", 0, "Sword", "d", 1, 2, 0, ann.ToString(), 3, 4, 5, 0, 6, Guid.Empty.ToString(), 0, 7, Guid.NewGuid(), ann, Guid.NewGuid(), 8);
        var opened = store.Open(Notice, true, n => Build(n, ann) with
        {
            Items = [new CaseItem(row.ItemId, ann, "Sword", "d", 0, ann.ToString(), "Ann Holder", "My Inventory", "a", row, null, null, "its stored file is missing",
                _time.GetUtcNow(), null)],
            Events = [new CaseEvent(_time.GetUtcNow(), "Test Admin", Guid.NewGuid(), "opened", null)],
        });

        var again = Store().Get(opened.Number);

        Assert.NotNull(again);
        Assert.Equal(opened.Items, again.Items);
        Assert.Equal(opened.People, again.People);
        Assert.Equal(opened.Events, again.Events);
        Assert.Equal(opened.Notice, again.Notice);
    }

    [Fact]
    public void A_damaged_case_file_makes_the_store_unreadable_and_is_left_in_place()
    {
        var number = Store().Open(Notice, true, n => Build(n)).Number;
        var file = Path.Combine(_root, number, "case.json");
        File.WriteAllText(file, "{ not json");

        var store = Store();

        Assert.True(store.Unreadable);
        Assert.Equal("{ not json", File.ReadAllText(file));
    }

    [Fact]
    public void Naming_finds_only_cases_that_list_the_person()
    {
        var store = Store();
        var ann = Guid.NewGuid();
        var mine = store.Open(Notice, true, n => Build(n, ann)).Number;
        store.Open(Notice, true, n => Build(n, Guid.NewGuid()));

        Assert.Equal([mine], store.Naming(ann).Select(c => c.Number));
    }

    [Fact]
    public void Case_folder_is_private_on_linux()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        if (OperatingSystem.IsWindows()) return;
        var number = Store().Open(Notice, true, n => Build(n)).Number;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(_root, number)));
    }
}
