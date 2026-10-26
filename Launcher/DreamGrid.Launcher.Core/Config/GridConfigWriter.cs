using DreamGrid.Launcher.Core.Ini;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Config;

/// <summary>Turns the prototype INIs in the staged runtime into the Robust and region INIs.</summary>
public sealed class GridConfigWriter(LauncherPaths paths)
{
    private static readonly string[] ThirdParties = ["outworldz.com", "hyperica.com", "gloebit.com"];

    public void Write(LauncherSettings s, IReadOnlyList<IIniContributor> contributors)
    {
        var host = HostAddress.Effective(s.Hostname);
        var rt = paths.RuntimeOpenSim;
        var extra = s.Overrides.Concat(contributors.SelectMany(c => c.Contribute(s))).ToList();

        // 1. Robust.HG.ini
        var robust = LoadProto(Path.Combine(rt, "Robust.HG.ini.proto"));
        robust.Set("Const", "GridName", s.GridName);
        robust.Set("Const", "http_listener_port", s.RobustPublicPort.ToString());
        robust.Set("Const", "BaseURL", $"http://{host}");
        robust.Set("Const", "PrivURL", "http://127.0.0.1");
        robust.Set("Const", "PublicPort", s.RobustPublicPort.ToString());
        robust.Set("Const", "PrivatePort", s.RobustPrivatePort.ToString());
        robust.Set("Const", "DiagnosticsPort", s.WebPort.ToString());
        robust.Set("Const", "MachineID", s.MachineId);
        robust.Set("DatabaseService", "StorageProvider", "\"OpenSim.Data.MySQL.dll\"");
        robust.Set("DatabaseService", "ConnectionString", $"\"{DbConn(s, "robust", "robustuser", s.Db.RobustPassword)}\"");
        SetNetwork(robust, s, "ConsolePort", s.RobustConsolePort);
        SetFsAssetsDirectories(robust);
        SetSmartStartAndWifi(robust, s);
        robust.Set("GridInfoService", "welcome", $"http://{host}:{s.WebPort}/welcome");
        robust.Set("GridInfoService", "gridname", s.GridName);
        Finish(robust, "robust", extra, Path.Combine(rt, "Robust.HG.ini"));

        // 2. GridCommon.ini
        var common = LoadProto(Path.Combine(rt, "config-include", "Proto", "Robust", "GridCommon.ini"));
        common.Set("DatabaseService", "StorageProvider", "\"OpenSim.Data.MySQL.dll\"");
        common.Set("DatabaseService", "ConnectionString", $"\"{DbConn(s, "opensim", "opensimuser", s.Db.OpensimPassword)}\"");
        Finish(common, "region", extra, Path.Combine(rt, "config-include", "GridCommon.ini"), applyExtra: false);

        // 3. FlotsamCache.ini
        var flotsam = LoadProto(Path.Combine(rt, "config-include", "FlotsamCache.ini.example"));
        flotsam.Set("AssetCache", "CacheDirectory", Fwd(paths.AssetCache));
        Finish(flotsam, "region", extra, Path.Combine(rt, "config-include", "FlotsamCache.ini"), applyExtra: false);

        if (s.Region is null) return;
        var r = s.Region;

        // 4. region OpenSim.ini
        var os = LoadProto(Path.Combine(rt, "config-include", "Proto", "Robust", "OpenSim.ini"));
        os.Set("Const", "BaseHostname", host);
        os.Set("Const", "BaseURL", $"http://{host}");
        os.Set("Const", "PublicPort", s.RobustPublicPort.ToString());
        os.Set("Const", "PrivatePort", s.RobustPrivatePort.ToString());
        os.Set("Const", "PrivURL", "http://127.0.0.1");
        os.Set("Const", "http_listener_port", s.RegionPort.ToString());
        os.Set("Const", "RegionFolderName", r.Instance);
        os.Set("Const", "DiagnosticsPort", s.WebPort.ToString());
        os.Set("Startup", "regionload_regionsdir", Fwd(paths.RegionDefinitionDir(r.Instance)) + "/");
        os.Set("Startup", "PIDFile", Fwd(Path.Combine(paths.Pids, r.Instance + ".pid")));
        os.Set("Startup", "ConsoleHistoryFile", Fwd(Path.Combine(paths.RegionDir(r.Instance), "ConsoleHistory.txt")));
        os.Set("Startup", "physics", r.Physics);
        os.Set("Startup", "meshing", r.Physics == "BulletSim" ? "Meshmerizer" : "ubODEMeshmerizer");
        // The region reads [Network] console_port (OpenSim.cs), unlike Robust's ConsolePort.
        SetNetwork(os, s, "console_port", s.RegionConsolePort);
        if (s.Setup.OwnerCreated && s.Setup.Owner is { } owner)
        {
            os.Set("Estates", "DefaultEstateName", $"{s.GridName} Estate");
            os.Set("Estates", "DefaultEstateOwnerName", $"{owner.FirstName} {owner.LastName}");
            os.Set("Estates", "DefaultEstateOwnerUUID", owner.PrincipalId.ToString());
        }
        else
        {
            // No owner yet: OpenSim asks for estate details when these are absent, so none may be active.
            os.CommentOut("Estates", "DefaultEstateOwnerName");
            os.CommentOut("Estates", "DefaultEstateOwnerUUID");
        }
        os.Set("RegionReady", "enabled", "true");
        os.Set("RegionReady", "alert_uri", $"http://127.0.0.1:{s.WebPort}/dreamgrid/regionready");
        SetSmartStartAndWifi(os, s);
        os.Set("YEngine", "ScriptEnginesPath", Fwd(paths.ScriptEngines));
        Finish(os, "region", extra, Path.Combine(paths.RegionDir(r.Instance), "OpenSim.ini"));

        // 5. region definition file
        var def = IniDocument.Parse("");
        def.Set(r.Name, "RegionUUID", r.Uuid.ToString());
        def.Set(r.Name, "Location", $"{r.X},{r.Y}");
        def.Set(r.Name, "InternalAddress", "0.0.0.0");
        def.Set(r.Name, "InternalPort", s.RegionPort.ToString());
        def.Set(r.Name, "ExternalHostName", host);
        def.Set(r.Name, "SizeX", "256");
        def.Set(r.Name, "SizeY", "256");
        Finish(def, "region", extra, Path.Combine(paths.RegionDefinitionDir(r.Instance), r.Name + ".ini"), applyExtra: false);
    }

    /// <summary>
    /// Loads a prototype, commenting out every line Nini cannot parse (not blank, a comment, a section or
    /// key=value). Robust.HG.ini.proto starts with a stray "3; *" that would abort Robust's boot.
    /// </summary>
    private static IniDocument LoadProto(string path)
    {
        var text = File.ReadAllText(path);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            var ok = t.Length == 0 || t[0] is ';' or '#' || (t[0] == '[' && t.IndexOf(']') > 0) || t.IndexOf('=') > 0;
            if (!ok) lines[i] = "; " + lines[i];
        }
        return IniDocument.Parse(string.Join('\n', lines));
    }

    private static string DbConn(LauncherSettings s, string db, string user, string pw) =>
        $"Data Source=127.0.0.1;Database={db};Port={s.MariaDbPort};User ID={user};Password={pw};Old Guids=true;Allow Zero Datetime=true;Connect Timeout=60;";

    private static string Fwd(string path) => path.Replace('\\', '/');

    private static void SetNetwork(IniDocument doc, LauncherSettings s, string portKey, int consolePort)
    {
        doc.Set("Network", "ConsoleUser", s.Console.User);
        doc.Set("Network", "ConsolePass", s.Console.Password);
        doc.Set("Network", portKey, consolePort.ToString());
    }

    private static void SetSmartStartAndWifi(IniDocument doc, LauncherSettings s)
    {
        doc.Set("SmartStart", "Enabled", "False");
        doc.Set("SmartStart", "URL", $"http://127.0.0.1:{s.WebPort}");
        doc.Set("SmartStart", "MachineID", s.MachineId);
        doc.Set("WifiService", "Enabled", "false");
    }

    /// <summary>Sets the FSAssets directories in the section whose BaseDirectory points at fsassets, leaving the bakes one alone.</summary>
    private void SetFsAssetsDirectories(IniDocument doc)
    {
        var section = doc.ActiveEntries()
            .Where(e => e.Key.Equals("BaseDirectory", StringComparison.OrdinalIgnoreCase)
                        && e.Value.Contains("fsassets", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Section)
            .LastOrDefault() ?? "AssetService";
        doc.Set(section, "BaseDirectory", Fwd(paths.FsAssetsData));
        doc.Set(section, "SpoolDirectory", Fwd(paths.FsAssetsSpool));
    }

    private static void Finish(IniDocument doc, string target, List<IniOverride> extra, string path, bool applyExtra = true)
    {
        foreach (var (section, key, value) in doc.ActiveEntries().ToList())
        {
            if (key.Equals("economymodule", StringComparison.OrdinalIgnoreCase))
                doc.Set(section, key, "");
            else if (ThirdParties.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase)))
                doc.CommentOut(section, key);
        }
        if (applyExtra)
            foreach (var o in extra.Where(o => o.Target.Equals(target, StringComparison.OrdinalIgnoreCase)))
                doc.Set(o.Section, o.Key, o.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        doc.Save(path);
    }
}
