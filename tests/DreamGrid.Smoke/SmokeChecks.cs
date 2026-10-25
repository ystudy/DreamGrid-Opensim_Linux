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
