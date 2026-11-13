namespace GridGate.Launcher.Core.Runtime;

/// <summary>
/// Names of build output left behind by the Wifi, Diva and jOpenSim add-ons, which were removed
/// (<c>docs/wifi-diva-removal-log.md</c>). A checkout built before then still has their files in <c>bin/</c>;
/// staging and packaging must never pick them up. <c>setup/package.ps1</c> applies the same rules.
/// </summary>
public static class RetiredFiles
{
    private static readonly string[] FilePrefixes = ["Diva.", "Wifi.", "WhoGotWhat.", "jOpenSim"];

    /// <summary>True for a bare file name that belongs to a retired add-on.</summary>
    public static bool IsRetiredFile(string name) =>
        FilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a bare folder name that belongs to a retired add-on.</summary>
    public static bool IsRetiredDirectory(string name) =>
        name.Equals("DivaUploads", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("WifiPages", StringComparison.OrdinalIgnoreCase);
}
