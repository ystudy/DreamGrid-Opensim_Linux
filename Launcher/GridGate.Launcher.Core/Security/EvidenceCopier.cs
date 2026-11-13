using System.Security.Cryptography;

namespace GridGate.Launcher.Core.Security;

/// <summary>Where an evidence copy went (relative to the case folder) and its SHA-256, or why there isn't one.</summary>
public sealed record EvidenceResult(string? File, string? Sha256, string? NotKept);

/// <summary>
/// Copies an asset's stored file from the FSAssets store into a case's <c>assets</c> folder, byte for byte. FSAssets keeps it at
/// <c>&lt;base&gt;/hash[0..2]/hash[2..4]/hash[4..6]/hash[6..10]/&lt;hash&gt;.gz</c> (its default layout), or as a plain <c>&lt;hash&gt;</c>
/// while it is still being written. The copy keeps the extension, so it is read the same way.
/// </summary>
public sealed class EvidenceCopier(string fsAssetsData)
{
    public const string NoRecord = "the asset store has no record of it";
    public const string Missing = "its stored file is missing";

    public EvidenceResult Copy(string? hash, string assetId, string caseFolder)
    {
        if (hash is null || hash.Length < 10) return new(null, null, NoRecord);
        var dir = Path.Combine(fsAssetsData, hash[..2], hash[2..4], hash[4..6], hash[6..10]);
        var (source, ext) = File.Exists(Path.Combine(dir, hash + ".gz")) ? (Path.Combine(dir, hash + ".gz"), ".gz")
            : File.Exists(Path.Combine(dir, hash)) ? (Path.Combine(dir, hash), "") : (null, "");
        if (source is null) return new(null, null, Missing);
        var relative = Path.Combine("assets", assetId + ext);
        try
        {
            var target = Path.Combine(caseFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            using var s = File.OpenRead(target);
            return new(relative, Convert.ToHexStringLower(SHA256.HashData(s)), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, null, $"it couldn't be copied ({e.GetType().Name})");
        }
    }
}
