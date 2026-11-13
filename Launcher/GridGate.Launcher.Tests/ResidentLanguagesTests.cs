using GridGate.Launcher.Core;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The language each resident chose, kept in guests/languages.json (plan 16a).</summary>
public sealed class ResidentLanguagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-languages-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "guests", "languages.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Set_then_Get_returns_it()
    {
        var id = Guid.NewGuid();
        var store = new ResidentLanguages(File_);
        store.Set(id, "de");
        Assert.Equal("de", store.Get(id));
    }

    [Fact]
    public void Get_unknown_is_null()
    {
        Assert.Null(new ResidentLanguages(File_).Get(Guid.NewGuid()));
    }

    [Fact]
    public void Set_twice_keeps_the_last()
    {
        var id = Guid.NewGuid();
        var store = new ResidentLanguages(File_);
        store.Set(id, "de");
        store.Set(id, "fr");
        Assert.Equal("fr", store.Get(id));
    }

    [Fact]
    public void Remove_forgets()
    {
        var id = Guid.NewGuid();
        var store = new ResidentLanguages(File_);
        store.Set(id, "de");
        store.Remove(id);
        Assert.Null(store.Get(id));
        store.Remove(id);
    }

    [Fact]
    public void Survives_a_new_instance_on_the_same_file()
    {
        var id = Guid.NewGuid();
        new ResidentLanguages(File_).Set(id, "es");
        Assert.Equal("es", new ResidentLanguages(File_).Get(id));
    }

    [Fact]
    public void CountUsing_counts_only_that_code()
    {
        var store = new ResidentLanguages(File_);
        store.Set(Guid.NewGuid(), "de");
        store.Set(Guid.NewGuid(), "de");
        store.Set(Guid.NewGuid(), "fr");
        Assert.Equal(2, store.CountUsing("de"));
        Assert.Equal(1, store.CountUsing("fr"));
        Assert.Equal(0, store.CountUsing("es"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("")]
    [InlineData("zz-not-a-language")]
    [InlineData("de\u0000")]
    public void Set_refuses_a_code_that_is_not_a_language(string code)
    {
        var id = Guid.NewGuid();
        var store = new ResidentLanguages(File_);
        Assert.Throws<ArgumentException>(() => store.Set(id, code));
        Assert.Null(store.Get(id));
    }

    [Fact]
    public void Set_accepts_a_language_that_is_not_built_in()
    {
        var id = Guid.NewGuid();
        var store = new ResidentLanguages(File_);
        store.Set(id, "it");
        Assert.Equal("it", store.Get(id));
    }

    [Fact]
    public void A_damaged_file_reads_as_empty_and_is_moved_aside_on_the_next_write()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "{ not json");
        var store = new ResidentLanguages(File_);

        Assert.Null(store.Get(id));
        store.Set(id, "ru");

        Assert.Equal("ru", new ResidentLanguages(File_).Get(id));
        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(File_)!, "languages.json.damaged-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
    }
}
