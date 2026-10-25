namespace DreamGrid.Smoke;

public sealed record CheckResult(string Id, bool Passed, string Detail);

/// <summary>Pure checks over a smoke run's log text and sandbox directory.</summary>
public static class SmokeChecks
{
    // Signs that a .NET 10 / Linux runtime gap was hit: BinaryFormatter, System.Drawing/libgdiplus, native libs.
    static readonly string[] Forbidden =
    {
        "PlatformNotSupportedException",
        "TypeInitializationException",
        "DllNotFoundException",
        "gdiplus",
        "Unable to load shared library",
        // FlotsamAssetCache logs only e.Message, never the exception type, when BinaryFormatter is missing.
        "BinaryFormatter serialization and deserialization have been removed",
        "Could not load file or assembly 'System.Runtime.Serialization.Formatters",
    };

    /// <summary>C1: RegionReadyModule prints this once the region accepts logins.</summary>
    public static CheckResult LoginsEnabled(string log)
    {
        bool ok = log.Contains("INITIALIZATION COMPLETE FOR", StringComparison.Ordinal)
               && log.Contains("LOGINS ENABLED", StringComparison.Ordinal);
        return new CheckResult("C1", ok, ok ? "region reported LOGINS ENABLED" : "no 'INITIALIZATION COMPLETE ... LOGINS ENABLED' line");
    }

    /// <summary>C2: none of the runtime-gap exceptions appear anywhere in the log.</summary>
    public static CheckResult NoForbiddenExceptions(string log)
    {
        var hits = Forbidden.Where(f => log.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        return new CheckResult("C2", hits.Count == 0, hits.Count == 0 ? "no forbidden exceptions" : "found: " + string.Join(", ", hits));
    }

    // Start of every BinaryFormatter stream (SerializedStreamHeader record, root id 1, header id -1).
    static readonly byte[] BinaryFormatterHeader = { 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>
    /// C4: the Flotsam disk cache holds real BinaryFormatter entries. Counting files is not enough: Flotsam creates
    /// the file before serializing, so a failed Serialize leaves empty files behind and logs nothing.
    /// *.fac files are Flotsam's access stamps, not assets.
    /// </summary>
    public static CheckResult AssetCacheHasEntries(string dir)
    {
        int entries = 0, other = 0;
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".fac", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (StartsWith(file, BinaryFormatterHeader)) entries++;
                else other++;
            }
        }
        return new CheckResult("C4", entries > 0,
            entries > 0 ? $"{entries} cached asset(s) in {Path.GetFileName(dir)}"
                        : $"no BinaryFormatter cache entries in {dir} ({other} other file(s))");
    }

    /// <summary>C6: the second boot reaches logins and reuses the estate from the first, rather than making a new one.</summary>
    public static CheckResult EstateReused(string secondBootLog)
    {
        if (!LoginsEnabled(secondBootLog).Passed)
            return new CheckResult("C6", false, "second boot: no 'INITIALIZATION COMPLETE ... LOGINS ENABLED' line");
        bool recreated = secondBootLog.Contains("is not part of an estate", StringComparison.Ordinal)
                      || secondBootLog.Contains("No existing estates found", StringComparison.Ordinal);
        return new CheckResult("C6", !recreated,
            recreated ? "second boot created a new estate: the first boot's database was not reused"
                      : "second boot reused the estate and reached LOGINS ENABLED");
    }

    static bool StartsWith(string path, byte[] prefix)
    {
        var buffer = new byte[prefix.Length];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(buffer, prefix.Length, throwOnEndOfStream: false) == prefix.Length
            && buffer.AsSpan().SequenceEqual(prefix);
    }

    public static CheckResult FileNonEmpty(string id, string path)
    {
        bool ok = File.Exists(path) && new FileInfo(path).Length > 0;
        return new CheckResult(id, ok, ok ? $"{Path.GetFileName(path)} written" : $"{path} missing or empty");
    }

    public static CheckResult DirectoryHasFiles(string id, string dir)
    {
        int count = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count() : 0;
        return new CheckResult(id, count > 0, count > 0 ? $"{count} file(s) in {Path.GetFileName(dir)}" : $"{dir} has no files");
    }
}
