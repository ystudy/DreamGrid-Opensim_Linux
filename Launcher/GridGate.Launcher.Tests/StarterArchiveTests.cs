using GridGate.Launcher.Core.Iar;
using GridGate.Launcher.Core.Starter;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Finding the avatars in an archive and cutting one out (spec 2026-11-11-starter-avatars-design §6.2).</summary>
public sealed class StarterArchiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gg-starterarc-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>A FULLAVATARS-style archive: avatars two levels down, each with body parts, clothes and a picture.</summary>
    internal static IarBuilder Avatars(params string[] names)
    {
        var b = new IarBuilder();
        foreach (var n in names)
        {
            var f = "FULLAVATARS/" + n;
            var shape = Guid.NewGuid();
            b.Asset(shape, "bodypart.txt", "LLWearable version 22\nshape"u8.ToArray());
            b.Item(f, "Shape", shape, 13, 18);
            var shirt = Guid.NewGuid();
            b.Asset(shirt, "clothing.txt", "LLWearable version 22\nshirt"u8.ToArray());
            b.Item(f + "/Clothes", "Shirt", shirt, 5, 18);
            b.Texture(f, n.Split(' ')[0]);
        }
        return b;
    }

    [Fact]
    public void Candidates_are_folders_with_body_parts()
    {
        var b = Avatars("Lyra complete female avatar", "Leo complete male avatar", "Professional Lady #1");
        b.Notecard("FULLAVATARS/Notes only", "Read me", "hello");
        b.Notecard("Top", "Loose", "x");
        var found = StarterArchive.Candidates(b.Save(P("a.iar")));
        Assert.Equal(["Lyra complete female avatar", "Leo complete male avatar", "Professional Lady #1"], found.Select(c => c.Name));
        Assert.Equal("FULLAVATARS/Lyra complete female avatar", found[0].FolderPath);
        Assert.Equal(3, found[0].Items); // shape, shirt (in a sub-folder) and the picture
    }

    [Fact]
    public void One_avatar_archive_keeps_only_its_folder()
    {
        var b = Avatars("Lyra complete female avatar", "Leo complete male avatar");
        var source = b.Save(P("a.iar"));
        StarterArchive.OneAvatar(source, "FULLAVATARS/Lyra complete female avatar", P("lyra.iar"));
        var kept = IarArchive.Inspect(P("lyra.iar"), 1 << 24, 100);
        Assert.Equal(["Shape", "Shirt", "Lyra"], kept.Items.Select(i => i.Name));
        Assert.All(kept.Items, i => Assert.StartsWith("FULLAVATARS/Lyra complete female avatar", i.FolderPath));
        Assert.Equal(3, kept.AssetEntries.Count); // Lyra's shape, shirt and picture; Leo's are gone
        // Folders: FULLAVATARS, Lyra's and Lyra's Clothes; never Leo's, empty.
        Assert.Equal(3, kept.Folders);
    }

    [Theory]
    [InlineData("Professional Lady #1", "Professional Lady 1")]
    [InlineData("Lyra complete female avatar", "Lyra complete female avatar")]
    [InlineData("###", "Starter avatar")]
    [InlineData("-dash", "dash")]
    public void Folder_names_are_made_safe(string name, string folder)
    {
        Assert.Equal(folder, StarterArchive.SafeFolder(name));
        Assert.True(IarFolderNames.IsSafe(StarterArchive.SafeFolder(name)));
    }
}
