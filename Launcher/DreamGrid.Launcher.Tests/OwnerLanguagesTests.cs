using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Time.Testing;
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
}
