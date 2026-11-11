using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The store for languages the grid owner adds (plan 16b).</summary>
public sealed class OwnerLanguagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-owner-langs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private string Folder => Path.Combine(_dir, "languages");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private OwnerLanguage Sample(string code) => new(code,
        new Dictionary<string, string> { ["welcome"] = "Benvenuto" },
        new Dictionary<string, string> { ["subject"] = "Ciao" },
        _time.GetUtcNow());

    [Fact]
    public void Save_then_Get_round_trips()
    {
        var store = new OwnerLanguages(Folder, _time);
        Assert.Null(store.Get("it"));
        store.Save(Sample("it"));
        var got = store.Get("it")!;
        Assert.Equal("it", got.Code);
        Assert.Equal("Benvenuto", got.Page["welcome"]);
        Assert.Equal("Ciao", got.Email["subject"]);
        Assert.Equal(_time.GetUtcNow(), got.Saved);
    }

    [Fact]
    public void Codes_lists_saved_languages_sorted()
    {
        var store = new OwnerLanguages(Folder, _time);
        Assert.Empty(store.Codes());
        store.Save(Sample("it"));
        store.Save(Sample("ar"));
        store.Save(Sample("pt-PT"));
        Assert.Equal(["ar", "it", "pt-PT"], store.Codes());
    }

    [Fact]
    public void Delete_removes_the_file()
    {
        var store = new OwnerLanguages(Folder, _time);
        store.Save(Sample("it"));
        Assert.True(store.Delete("it"));
        Assert.False(File.Exists(Path.Combine(Folder, "it.json")));
        Assert.Null(store.Get("it"));
        Assert.False(store.Delete("it"));
    }

    [Fact]
    public void Survives_a_new_instance()
    {
        new OwnerLanguages(Folder, _time).Save(Sample("it"));
        Assert.Equal("Benvenuto", new OwnerLanguages(Folder, _time).Get("it")!.Page["welcome"]);
    }

    [Theory]
    [InlineData("it", "it")]
    [InlineData("IT", "it")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("ar", "ar")]
    [InlineData("de", null)]
    [InlineData("en", null)]
    [InlineData("xx", null)]
    [InlineData("../x", null)]
    [InlineData("", null)]
    public void CodeFor_cases(string typed, string? expected) => Assert.Equal(expected, OwnerLanguages.CodeFor(typed));

    [Fact]
    public void Save_refuses_a_built_in_code() =>
        Assert.Throws<ArgumentException>(() => new OwnerLanguages(Folder, _time).Save(Sample("de")));

    [Fact]
    public void Save_refuses_a_code_that_is_not_a_culture()
    {
        var store = new OwnerLanguages(Folder, _time);
        Assert.Throws<ArgumentException>(() => store.Save(Sample("xx")));
        Assert.Throws<ArgumentException>(() => store.Save(Sample("../x")));
        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any());
    }

    [Fact]
    public void A_damaged_file_reads_as_absent_and_is_moved_aside()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "it.json"), "{ not json");
        var store = new OwnerLanguages(Folder, _time);
        Assert.Null(store.Get("it"));
        Assert.Empty(store.Codes());
        Assert.False(File.Exists(Path.Combine(Folder, "it.json")));
        Assert.Single(Directory.GetFiles(Folder, "it.json.damaged-*"));
    }

    [Fact]
    public void The_file_name_comes_from_the_code_only()
    {
        new OwnerLanguages(Folder, _time).Save(Sample("it"));
        Assert.Equal([Path.Combine(Folder, "it.json")], Directory.GetFileSystemEntries(Folder));
    }

    [Fact]
    public void A_changed_file_is_read_again()
    {
        var store = new OwnerLanguages(Folder, _time);
        store.Save(Sample("it"));
        Assert.Equal("Benvenuto", store.Get("it")!.Page["welcome"]);
        new OwnerLanguages(Folder, _time).Save(Sample("it") with { Page = new Dictionary<string, string> { ["welcome"] = "Salve a tutti" } });
        Assert.Equal("Salve a tutti", store.Get("it")!.Page["welcome"]);
    }

    [Fact]
    public void OfferedOrDefault_keeps_added_codes()
    {
        var s = new LanguageSettings { Offered = ["en", "it"] };
        Assert.Contains("it", s.OfferedOrDefault(["it"]));
        Assert.DoesNotContain("it", s.OfferedOrDefault([]));
    }

    [Fact]
    public void A_null_wording_in_the_file_reads_as_absent()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "it.json"),
            """{"code":"it","page":{"a":"Uno","b":null},"email":{"c":null,"d":"Quattro"},"saved":"2026-11-11T12:00:00+00:00"}""");
        var got = new OwnerLanguages(Folder, _time).Get("it")!;
        Assert.Equal(["a"], got.Page.Keys);
        Assert.Equal(["d"], got.Email.Keys);
    }

    [Fact]
    public void Codes_looks_at_the_folder_once_a_second()
    {
        var store = new OwnerLanguages(Folder, _time);
        Assert.Empty(store.Codes());
        // Another process (here: another instance) adds a file.
        new OwnerLanguages(Folder, _time).Save(Sample("it"));
        Assert.Empty(store.Codes());
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Empty(store.Codes());
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["it"], store.Codes());
    }

    [Fact]
    public void Save_and_Delete_refresh_the_list_at_once()
    {
        var store = new OwnerLanguages(Folder, _time);
        Assert.Empty(store.Codes());
        store.Save(Sample("it"));
        Assert.Equal(["it"], store.Codes());
        Assert.True(store.Delete("it"));
        Assert.Empty(store.Codes());
    }

    private sealed class Warnings : ILogger<OwnerLanguages>
    {
        public int Count;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Count++;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Codes_survives_an_unreadable_folder(bool denied)
    {
        var log = new Warnings();
        var store = new OwnerLanguages(Folder, _time, log);
        store.Save(Sample("it"));
        store.ListFiles = _ => throw (denied ? new UnauthorizedAccessException() : new IOException("disk"));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(store.Codes());
        Assert.Equal(1, log.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Delete_survives_a_file_that_cannot_be_removed(bool denied)
    {
        var log = new Warnings();
        var store = new OwnerLanguages(Folder, _time, log);
        store.Save(Sample("it"));
        store.RemoveFile = _ => throw (denied ? new UnauthorizedAccessException() : new IOException("locked"));
        Assert.False(store.Delete("it"));
        Assert.Equal(1, log.Count);
    }
}
