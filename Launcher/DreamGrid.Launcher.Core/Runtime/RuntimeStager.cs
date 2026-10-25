using System.Xml;

namespace DreamGrid.Launcher.Core.Runtime;

/// <summary>Stages a runtime copy of the program's OpenSim bin under the data directory.</summary>
public sealed class RuntimeStager(LauncherPaths paths)
{
    private static readonly string[] ExcludedDirectories =
        ["assetcache", "Regions", "ScriptEngines", "fsassets", "MeshCache", "j2kDecodeCache", "maptiles", "bakes"];

    private static readonly string[] ExcludedFiles = ["Gloebit.dll"];

    // The program's live OpenSim.ini; config-include/Proto/Robust/OpenSim.ini is the region prototype and is staged.
    private const string TopLevelOnlyExcluded = "OpenSim.ini";

    private static readonly string[] ExcludedExtensions = [".log", ".db", ".pid"];

    private string StampFile => Path.Combine(paths.RuntimeOpenSim, ".dreamgrid-stamp");

    /// <summary>Copies the program dir into the runtime dir when its version stamp changed. Returns true if it copied.</summary>
    public bool StageIfNeeded()
    {
        var stamp = ComputeStamp();
        if (File.Exists(StampFile) && File.ReadAllText(StampFile) == stamp)
            return false;

        if (Directory.Exists(paths.RuntimeOpenSim))
            Directory.Delete(paths.RuntimeOpenSim, true);
        Directory.CreateDirectory(paths.RuntimeOpenSim);

        CopyDirectory(paths.OpenSimProgram, paths.RuntimeOpenSim, topLevel: true);
        File.WriteAllText(StampFile, stamp);
        return true;
    }

    /// <summary>True when the program dir changed since the last staging (or nothing is staged yet).</summary>
    public bool NeedsStaging() => !(File.Exists(StampFile) && File.ReadAllText(StampFile) == ComputeStamp());

    /// <summary>Writes the log4net configs for Robust and (optionally) one region instance.</summary>
    public void WriteLogConfigs(string? regionInstance, string level = "INFO")
    {
        Directory.CreateDirectory(paths.RuntimeOpenSim);
        WriteLogConfig(Path.Combine(paths.RuntimeOpenSim, "dreamgrid-robust.log4net.xml"),
            Path.Combine(paths.Logs, "robust.log"), level);
        if (!string.IsNullOrEmpty(regionInstance))
            WriteLogConfig(Path.Combine(paths.RuntimeOpenSim, $"dreamgrid-region-{regionInstance}.log4net.xml"),
                Path.Combine(paths.Logs, $"region-{regionInstance}.log"), level);
    }

    private string ComputeStamp()
    {
        var program = paths.OpenSimProgram;
        var count = Directory.EnumerateFiles(program).Count();
        // The INI prototypes and includes are what GridConfigWriter builds the configs from.
        var include = Path.Combine(program, "config-include");
        var configs = Directory.EnumerateFiles(program, "*.proto")
            .Concat(Directory.Exists(include) ? Directory.EnumerateFiles(include, "*", SearchOption.AllDirectories) : [])
            .Select(f => File.GetLastWriteTimeUtc(f).Ticks)
            .DefaultIfEmpty(0)
            .Max();
        return $"{Describe("OpenSim.dll")}-{count}-{Describe("Robust.dll")}-{configs}";

        string Describe(string name)
        {
            var dll = new FileInfo(Path.Combine(program, name));
            return dll.Exists ? $"{dll.Length}-{dll.LastWriteTimeUtc.Ticks}" : "none";
        }
    }

    private static bool IsExcludedDirectory(string name) =>
        name.StartsWith("addin-db-", StringComparison.OrdinalIgnoreCase)
        || ExcludedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsExcludedFile(string name) =>
        ExcludedFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
        || ExcludedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string target, bool topLevel = false)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (!IsExcludedFile(name) && !(topLevel && name.Equals(TopLevelOnlyExcluded, StringComparison.OrdinalIgnoreCase)))
                File.Copy(file, Path.Combine(target, name), true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (!IsExcludedDirectory(name))
                CopyDirectory(dir, Path.Combine(target, name));
        }
    }

    private static void WriteLogConfig(string file, string logPath, string level)
    {
        using var w = XmlWriter.Create(file, new XmlWriterSettings { Indent = true });
        w.WriteStartDocument();
        w.WriteStartElement("log4net");
        w.WriteStartElement("appender");
        w.WriteAttributeString("name", "LogFileAppender");
        w.WriteAttributeString("type", "log4net.Appender.RollingFileAppender");
        Value(w, "file", logPath.Replace('\\', '/'));
        Value(w, "appendToFile", "true");
        Value(w, "rollingStyle", "Size");
        Value(w, "maximumFileSize", "10MB");
        Value(w, "maxSizeRollBackups", "5");
        Value(w, "staticLogFileName", "true");
        w.WriteStartElement("layout");
        w.WriteAttributeString("type", "log4net.Layout.PatternLayout");
        Value(w, "conversionPattern", "%date{HH:mm:ss} - %message%newline");
        w.WriteEndElement(); // layout
        w.WriteEndElement(); // appender
        w.WriteStartElement("root");
        Value(w, "level", level);
        w.WriteStartElement("appender-ref");
        w.WriteAttributeString("ref", "LogFileAppender");
        w.WriteEndElement();
        w.WriteEndElement(); // root
        w.WriteEndElement(); // log4net
        w.WriteEndDocument();
    }

    private static void Value(XmlWriter w, string element, string value)
    {
        w.WriteStartElement(element);
        w.WriteAttributeString("value", value);
        w.WriteEndElement();
    }
}
