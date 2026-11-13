using System.Security.Cryptography;
using GridGate.Launcher.Core.Security;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Copying an asset's stored FSAssets file into a case (spec 2026-11-04-inventory-takedown-design, Evidence).</summary>
public sealed class EvidenceCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-evidence-" + Guid.NewGuid().ToString("N"));
    private const string Hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string Asset = "11111111-2222-3333-4444-555555555555";

    private string Fs => Path.Combine(_root, "fsassets");
    private string Case => Path.Combine(_root, "TD-2026-001");

    public EvidenceCopierTests() => Directory.CreateDirectory(Path.Combine(Case, "assets"));

    public void Dispose() => Directory.Delete(_root, true);

    private string Stored(string name)
    {
        var dir = Path.Combine(Fs, "ab", "cd", "ef", "0123");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public void Copies_the_gz_file_byte_for_byte_with_its_sha256()
    {
        byte[] bytes = [1, 2, 3, 250];
        File.WriteAllBytes(Stored(Hash + ".gz"), bytes);

        var result = new EvidenceCopier(Fs).Copy(Hash, Asset, Case);

        Assert.Null(result.NotKept);
        Assert.Equal(Path.Combine("assets", Asset + ".gz"), result.File);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Case, "assets", Asset + ".gz")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Sha256);
    }

    [Fact]
    public void Copies_a_plain_spool_file_when_there_is_no_gz()
    {
        File.WriteAllBytes(Stored(Hash), [9]);

        var result = new EvidenceCopier(Fs).Copy(Hash, Asset, Case);

        Assert.Equal(Path.Combine("assets", Asset), result.File);
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(Case, "assets", Asset)));
    }

    [Fact]
    public void Missing_file_says_why()
    {
        var result = new EvidenceCopier(Fs).Copy(Hash, Asset, Case);
        Assert.Null(result.File);
        Assert.Equal("its stored file is missing", result.NotKept);
    }

    [Fact]
    public void Null_hash_says_no_record()
    {
        var result = new EvidenceCopier(Fs).Copy(null, Asset, Case);
        Assert.Null(result.File);
        Assert.Equal("the asset store has no record of it", result.NotKept);
    }
}
