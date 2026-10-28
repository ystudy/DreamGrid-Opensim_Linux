using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class OarLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-oars-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public OarLibraryTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        Directory.CreateDirectory(_paths.OarsIncoming);
        Directory.CreateDirectory(_paths.OarsSaving);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Add(string relative, DateTime? written = null, byte[]? bytes = null)
    {
        var path = Path.Combine(_paths.Oars, relative);
        File.WriteAllBytes(path, bytes ?? [1, 2, 3]);
        if (written is { } w) File.SetLastWriteTimeUtc(path, w);
        return path;
    }

    [Theory]
    [InlineData("../x.oar")] [InlineData("a/b.oar")] [InlineData("..\\x.oar")] [InlineData(".hidden.oar")]
    [InlineData("x.txt")] [InlineData("")] [InlineData("a\"b.oar")]
    [InlineData("a:b.oar")] [InlineData("a\0b.oar")] [InlineData("a\tb.oar")]
    public void Library_refuses_unsafe_names(string name)
    {
        Assert.False(OarLibrary.IsSafeName(name));
        Assert.Null(new OarLibrary(_paths).Resolve(name));
    }

    [Fact]
    public void A_name_over_120_characters_is_unsafe()
    {
        Assert.True(OarLibrary.IsSafeName(new string('a', 116) + ".oar"));
        Assert.False(OarLibrary.IsSafeName(new string('a', 117) + ".oar"));
    }

    [Fact]
    public void Resolve_finds_a_safe_existing_name_in_any_case_of_extension()
    {
        var a = Add("a.oar");
        var b = Add("b.OAR");
        var library = new OarLibrary(_paths);

        Assert.Equal(a, library.Resolve("a.oar"));
        Assert.Equal(b, library.Resolve("b.OAR"));
        Assert.Null(library.Resolve("missing.oar"));
    }

    [Fact]
    public void List_shows_only_top_level_oars_newest_first()
    {
        Add("a.oar", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Add("b.OAR", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), [1, 2, 3, 4, 5]);
        Add("notes.txt");
        Add(Path.Combine(".incoming", "c.oar"));

        var list = new OarLibrary(_paths).List();

        Assert.Equal(["b.OAR", "a.oar"], list.Select(e => e.Name));
        Assert.Equal(5, list[0].Bytes);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), list[0].Modified);
    }

    [Fact]
    public void List_is_empty_when_the_folder_does_not_exist()
    {
        Directory.Delete(_paths.Oars, true);

        Assert.Empty(new OarLibrary(_paths).List());
    }

    [Fact]
    public void FreeName_counts_up()
    {
        Add("Home-2026-10-28.oar");
        Add("Home-2026-10-28-2.oar");
        var library = new OarLibrary(_paths);

        Assert.Equal("Home-2026-10-28-3.oar", library.FreeName("Home-2026-10-28.oar"));
        Assert.Equal("fresh.oar", library.FreeName("fresh.oar"));
    }

    [Fact]
    public void FreeName_keeps_a_colliding_120_character_name_within_the_limit()
    {
        var name = new string('a', 116) + ".oar";
        Add(name);

        var free = new OarLibrary(_paths).FreeName(name);

        Assert.NotEqual(name, free);
        Assert.True(free.Length <= 120);
        Assert.True(OarLibrary.IsSafeName(free));
    }

    [Fact]
    public void List_includes_any_case_of_extension()
    {
        Add("c.Oar");
        Add("d.oAR");

        Assert.Equal(["c.Oar", "d.oAR"], new OarLibrary(_paths).List().Select(e => e.Name).Order());
    }

    [Fact]
    public void CleanName_maps_colon_and_control_characters()
        => Assert.Equal("a_b_c_d.oar", OarLibrary.CleanName("a:b\0c\td.oar"));

    [Theory]
    [InlineData("my world?.OAR", "my world_.OAR")]
    [InlineData("C:\\x\\land", "land.oar")]
    [InlineData("/tmp/a/castle.oar", "castle.oar")]
    [InlineData("a\"b<c>.oar", "a_b_c_.oar")]
    [InlineData("it's (1)-x_y.oar", "it's (1)-x_y.oar")]
    [InlineData(".hidden", "hidden.oar")]
    [InlineData("", "region.oar")]
    public void CleanName_makes_a_safe_name(string uploaded, string expected) => Assert.Equal(expected, OarLibrary.CleanName(uploaded));

    [Fact]
    public void CleanName_trims_to_120_characters_and_the_result_is_safe()
    {
        var cleaned = OarLibrary.CleanName(new string('a', 300) + ".oar");

        Assert.Equal(120, cleaned.Length);
        Assert.True(OarLibrary.IsSafeName(cleaned));
        Assert.True(OarLibrary.IsSafeName(OarLibrary.CleanName(new string('b', 300))));
    }

    [Fact]
    public void Delete_removes_a_library_file_and_ignores_unsafe_names()
    {
        var a = Add("a.oar");
        var outside = Path.Combine(_paths.Data, "keep.oar");
        File.WriteAllBytes(outside, [1]);
        var library = new OarLibrary(_paths);

        library.Delete("../keep.oar");
        library.Delete("missing.oar");
        Assert.True(File.Exists(outside));

        library.Delete("a.oar");
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void ClearStaging_empties_incoming_and_saving()
    {
        var kept = Add("keep.oar");
        Add(Path.Combine(".incoming", "u.oar"));
        Add(Path.Combine(".saving", "s.oar"));
        Directory.CreateDirectory(Path.Combine(_paths.OarsSaving, "sub"));
        File.WriteAllBytes(Path.Combine(_paths.OarsSaving, "sub", "x"), [1]);

        new OarLibrary(_paths).ClearStaging();

        Assert.Empty(Directory.GetFileSystemEntries(_paths.OarsIncoming));
        Assert.Empty(Directory.GetFileSystemEntries(_paths.OarsSaving));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void ClearStaging_without_the_folders_does_nothing()
    {
        Directory.Delete(_paths.Oars, true);

        new OarLibrary(_paths).ClearStaging();
    }
}
