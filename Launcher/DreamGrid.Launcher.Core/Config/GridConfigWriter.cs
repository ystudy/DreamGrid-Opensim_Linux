using System.Globalization;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Ini;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Config;

/// <summary>Turns the prototype INIs in the staged runtime into the Robust and region INIs.</summary>
public sealed class GridConfigWriter(LauncherPaths paths)
{
    private static readonly string[] ThirdParties = ["outworldz.com", "hyperica.com", "gloebit.com"];

    // What a preset grid's own OpenSim.ini decides for the regions: these whole sections, and these single keys.
    private static readonly string[] PresetSections = ["Groups", "UserProfiles", "XBakes", "Search"];
    private static readonly (string Section, string Key)[] PresetKeys =
        [
            ("Messaging", "OfflineMessageURL"), ("Messaging", "MuteListModule"), ("Messaging", "MuteListURL"),
            ("SimulatorFeatures", "MapTileURL"), ("ClientStack.LindenCaps", "Cap_GetTexture"),
        ];

    // The keys in GridCommon.ini that name the grid's gatekeeper and home address.
    private static readonly (string Section, string Key)[] GatekeeperKeys =
    [
        ("Hypergrid", "HomeURI"), ("Hypergrid", "GatekeeperURI"), ("GridService", "Gatekeeper"), ("Messaging", "Gatekeeper"),
        ("HGInventoryAccessModule", "HomeURI"), ("HGInventoryAccessModule", "Gatekeeper"), ("HGAssetService", "HomeURI"),
        ("UserAgentService", "UserAgentServerURI"),
    ];

    /// <summary>
    /// Writes Robust's INIs and every program's. <paramref name="held"/> are regions stopped for now: their program
    /// runs without them until a world start.
    /// </summary>
    public void Write(LauncherSettings s, IReadOnlyList<IIniContributor> contributors, IReadOnlySet<Guid>? held = null)
    {
        var host = HostAddress.Effective(s.Hostname);
        var rt = paths.RuntimeOpenSim;
        var hypergrid = PlanCatalog.HypergridOn(s);
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
        robust.Set("GatekeeperService", "ForeignAgentsAllowed", Bool(hypergrid));
        robust.Set("GridInfoService", "welcome", $"http://{host}:{s.WebPort}/welcome");
        // The viewer's "forgot password" link: the website's page, not Wifi's. Robust's own setpassword (AllowSetPassword) stays
        // off: the website writes the new password itself.
        // The viewer's "create account" link: the website's sign-up page (it says so when sign-up is closed).
        if (AdminRule.HasStaffPages(s))
        {
            robust.Set("GridInfoService", "password", $"http://{host}:{s.WebPort}/guest/forgot-password");
            robust.Set("GridInfoService", "register", $"http://{host}:{s.WebPort}/signup");
        }
        robust.Set("GridInfoService", "gridname", s.GridName);
        // What a Region Server reads to join this grid.
        robust.Set("GridInfoService", GridInfoKeys.Version, LauncherVersion.Current);
        robust.Set("GridInfoService", GridInfoKeys.PrivatePort, s.RobustPrivatePort.ToString());
        robust.Set("GridInfoService", GridInfoKeys.Hypergrid, Bool(hypergrid));
        robust.Set("GridInfoService", GridInfoKeys.RegionPorts, RegionPorts(s));
        robust.Set("GridInfoService", GridInfoKeys.Plan, s.Plan is { } plan ? PlanCatalog.Get(plan).Id : "");
        Finish(robust, "robust", extra, Path.Combine(rt, "Robust.HG.ini"));

        // 2. GridCommon.ini. A preset grid's own names each of its services; every other builds them from [Const].
        var preset = GridEndpoints.Preset(s);
        var common = LoadProto(Path.Combine(rt, "config-include", "Proto", preset?.ProtoFolder ?? "Robust", "GridCommon.ini"));
        common.Set("DatabaseService", "StorageProvider", "\"OpenSim.Data.MySQL.dll\"");
        common.Set("DatabaseService", "ConnectionString", $"\"{DbConn(s, "opensim", "opensimuser", s.Db.OpensimPassword)}\"");
        common.Set("GridService", "AllowHypergridMapSearch", Bool(hypergrid));
        if (preset is null) SetOtherGatekeeper(common, s);
        Finish(common, "region", extra, Path.Combine(rt, "config-include", "GridCommon.ini"), applyExtra: false);

        // 3. FlotsamCache.ini
        var flotsam = LoadProto(Path.Combine(rt, "config-include", "FlotsamCache.ini.example"));
        flotsam.Set("AssetCache", "CacheDirectory", Fwd(paths.AssetCache));
        Finish(flotsam, "region", extra, Path.Combine(rt, "config-include", "FlotsamCache.ini"), applyExtra: false);

        foreach (var program in s.Programs)
            WriteProgram(s, program, RegionModel.Loadable(s, program.Instance, held ?? NoRegions).ToList(), host, extra, hypergrid);

        // A deleted region whose program went keeps that folder; its region file goes off too.
        foreach (var r in s.Regions.Where(r => r.DeletedAt is not null && RegionModel.ProgramOf(s, r) is null))
        {
            var file = Path.Combine(paths.RegionDefinitionDir(r.Instance), r.Name + ".ini");
            if (File.Exists(file)) File.Move(file, file + ".off", overwrite: true);
        }
    }

    private static readonly IReadOnlySet<Guid> NoRegions = new HashSet<Guid>();

    /// <summary>
    /// The program's OpenSim.ini and one region file per loadable region. Any other region file in its Region folder
    /// becomes <c>.off</c>, so OpenSim never loads a region the launcher holds back (parked, stopped or deleted).
    /// </summary>
    private void WriteProgram(LauncherSettings s, ProgramSettings program, List<RegionSettings> regions, string host, List<IniOverride> extra, bool hypergrid)
    {
        var rt = paths.RuntimeOpenSim;
        var instance = program.Instance;

        // 4. region OpenSim.ini
        var os = LoadProto(Path.Combine(rt, "config-include", "Proto", "Robust", "OpenSim.ini"));
        os.Set("Architecture", "Include-Architecture", hypergrid ? "\"config-include/GridHypergrid.ini\"" : "\"config-include/Grid.ini\"");
        os.Set("Const", "BaseHostname", host);
        // On a Region Server BaseURL and PublicPort are the grid's published login address, PrivURL and PrivatePort the
        // Grid Asset Server's typed address; on a Connected Server they are the other grid's login and services
        // addresses. BaseHostname stays this computer.
        var (baseUrl, publicPort) = GridEndpoints.Advertised(s);
        os.Set("Const", "BaseURL", baseUrl);
        os.Set("Const", "PublicPort", publicPort.ToString());
        os.Set("Const", "PrivatePort", GridEndpoints.PrivatePort(s).ToString());
        os.Set("Const", "PrivURL", GridEndpoints.PrivateUrl(s));
        os.Set("Const", "http_listener_port", program.HttpPort.ToString());
        os.Set("Const", "RegionFolderName", instance);
        os.Set("Const", "DiagnosticsPort", s.WebPort.ToString());
        if (GridEndpoints.Preset(s) is { } preset)
            CopyFromPreset(os, LoadProto(Path.Combine(rt, "config-include", "Proto", preset.ProtoFolder, "OpenSim.ini")));
        os.Set("Startup", "regionload_regionsdir", Fwd(paths.RegionDefinitionDir(instance)) + "/");
        os.Set("Startup", "PIDFile", Fwd(Path.Combine(paths.Pids, instance + ".pid")));
        os.Set("Startup", "ConsoleHistoryFile", Fwd(Path.Combine(paths.RegionDir(instance), "ConsoleHistory.txt")));
        var physics = RegionSettingsRules.EffectivePhysics(s, program);
        os.Set("Startup", "physics", physics == "ubODE" ? "ubODE" : "BulletSim");
        os.Set("Startup", "meshing", physics == "ubODE" ? "ubODEMeshmerizer" : "Meshmerizer");
        if (physics != "ubODE") os.Set("BulletSim", "UseSeparatePhysicsThread", physics == "BulletSimThreaded" ? "true" : "false");
        WriteProgramOptions(os, s, program, regions);
        // The region reads [Network] console_port (OpenSim.cs), unlike Robust's ConsolePort.
        SetNetwork(os, s, "console_port", program.ConsolePort);
        if (s.Setup.OwnerCreated && s.Setup.Owner is { } owner)
        {
            os.Set("Estates", "DefaultEstateName", $"{s.GridName} Estate");
            os.Set("Estates", "DefaultEstateOwnerName", $"{owner.FirstName} {owner.LastName}");
            // The owner's id is the grid's to know; only an install running its own Robust carries it.
            if (!PlanCatalog.RunsRobust(s)) os.CommentOut("Estates", "DefaultEstateOwnerUUID");
            else os.Set("Estates", "DefaultEstateOwnerUUID", owner.PrincipalId.ToString());
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
        Finish(os, "region", extra, Path.Combine(paths.RegionDir(instance), "OpenSim.ini"));

        // 5. region definition files. Every other region file goes off first, by its exact name: after a rename that
        // only changes letter case ("home" to "Home") the old file must go off on Linux, and on Windows the new file
        // must not be written into the old one, which would keep the old name.
        var dir = paths.RegionDefinitionDir(instance);
        Directory.CreateDirectory(dir);
        var keep = regions.Select(r => r.Name + ".ini").ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.ini").ToList())
            if (!keep.Contains(Path.GetFileName(file)))
                File.Move(file, file + ".off", overwrite: true);
        foreach (var r in regions)
        {
            var def = IniDocument.Parse("");
            def.Set(r.Name, "RegionUUID", r.Uuid.ToString());
            def.Set(r.Name, "Location", $"{r.X},{r.Y}");
            def.Set(r.Name, "InternalAddress", "0.0.0.0");
            def.Set(r.Name, "InternalPort", r.UdpPort.ToString());
            def.Set(r.Name, "ExternalHostName", host);
            def.Set(r.Name, "SizeX", (256 * r.Size).ToString());
            def.Set(r.Name, "SizeY", (256 * r.Size).ToString());
            var ro = r.Options ?? new RegionOptions();
            if (ro.MaxAgents is { } agents) def.Set(r.Name, "MaxAgents", Num(agents));
            if (ro.MaxPrims is { } prims) def.Set(r.Name, "MaxPrims", Num(prims));
            if (ro.MaxNonPhysicalSize is { } nonPhys) def.Set(r.Name, "NonPhysicalPrimMax", Num(nonPhys));
            if (ro.MaxPhysicalSize is { } phys) def.Set(r.Name, "PhysicalPrimMax", Num(phys));
            if (ro.ClampSize) def.Set(r.Name, "ClampPrimSize", "True");
            Finish(def, "region", extra, Path.Combine(dir, r.Name + ".ini"), applyExtra: false);
        }
    }

    /// <summary>
    /// Copies what a preset grid decides for its regions from its <paramref name="preset"/> OpenSim.ini: each of
    /// <see cref="PresetSections"/> as a whole (a key the preset lacks goes off) and each of <see cref="PresetKeys"/>.
    /// </summary>
    private static void CopyFromPreset(IniDocument os, IniDocument preset)
    {
        foreach (var section in PresetSections)
        {
            var theirs = preset.ActiveEntries().Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();
            var ours = os.ActiveEntries().Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var (_, key, _) in ours.Where(o => !theirs.Any(t => t.Key.Equals(o.Key, StringComparison.OrdinalIgnoreCase))))
                os.CommentOut(section, key);
            foreach (var (_, key, value) in theirs)
                os.Set(section, key, value);
        }
        foreach (var (section, key) in PresetKeys)
        {
            if (preset.Get(section, key) is { } value) os.Set(section, key, value);
            else os.CommentOut(section, key);
        }
    }

    /// <summary>
    /// On a Connected Server whose grid's gatekeeper is not its login address, points the hypergrid keys at that
    /// gatekeeper instead of <c>BaseURL:PublicPort</c>.
    /// </summary>
    private static void SetOtherGatekeeper(IniDocument common, LauncherSettings s)
    {
        if (s.Plan != OwnerPlan.Connected || s.Connected?.GatekeeperUri is not { } uri) return;
        var gatekeeper = uri.TrimEnd('/');
        var (baseUrl, publicPort) = GridEndpoints.Advertised(s);
        if (SameAddress(gatekeeper, $"{baseUrl}:{publicPort}")) return;
        foreach (var (section, key) in GatekeeperKeys)
            common.Set(section, key, $"\"{gatekeeper}\"");
    }

    private static bool SameAddress(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
            ? x == y
            : a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The lowest and highest of every program's HTTP port and every live region's UDP port as "from-to"; empty without programs.</summary>
    private static string RegionPorts(LauncherSettings s)
    {
        var ports = s.Programs.Select(p => p.HttpPort).Concat(RegionModel.Live(s).Select(r => r.UdpPort)).ToList();
        return ports.Count == 0 ? "" : $"{ports.Min()}-{ports.Max()}";
    }

    private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool v) => v ? "true" : "false";

    /// <summary>Scripts, timers, frame rate, map, permissions, access and Concierge for one program.</summary>
    private static void WriteProgramOptions(IniDocument os, LauncherSettings s, ProgramSettings program, List<RegionSettings> regions)
    {
        var o = program.Options ?? new ProgramOptions();
        os.Set("YEngine", "Enabled", Bool(RegionSettingsRules.EffectiveScripts(s, program)));
        if (o.MinTimerInterval is { } timer) os.Set("YEngine", "MinTimerInterval", timer.ToString(CultureInfo.InvariantCulture));
        if (o.FramesPerSecond is { } fps) os.Set("Startup", "FrameTime", (1.0 / fps).ToString("0.0000", CultureInfo.InvariantCulture));

        // Every map key is written, so no prototype value leaks through.
        var map = o.Map;
        os.Set("Map", "GenerateMaptiles", Bool(map != MapQuality.None));
        if (map != MapQuality.None)
            os.Set("Map", "MapImageModule", map == MapQuality.Simple ? "\"MapImageModule\"" : "\"Warp3DImageModule\"");
        os.Set("Map", "TextureOnMapTile", Bool(map >= MapQuality.Better));
        os.Set("Map", "DrawPrimOnMapTile", Bool(map >= MapQuality.Better));
        os.Set("Map", "TexturePrims", Bool(map >= MapQuality.Best));
        os.Set("Map", "RenderMeshes", Bool(map >= MapQuality.Best));

        os.Set("Permissions", "allow_grid_gods", Bool(o.AllowGods));
        os.Set("Permissions", "region_owner_is_god", Bool(o.OwnerIsGod));
        os.Set("Permissions", "region_manager_is_god", Bool(o.ManagerIsGod));

        foreach (var r in regions.Where(r => r.Options?.KeepResidentsOut == true))
            os.Set("AuthorizationService", "Region_" + r.Name.Replace(' ', '_'), "DisallowResidents");

        var announced = regions.Where(r => r.Options?.AnnounceVisitors == true).Select(r => Regex.Escape(r.Name)).ToList();
        os.Set("Concierge", "enabled", Bool(announced.Count > 0));
        if (announced.Count > 0) os.Set("Concierge", "regions", $"\"^(?:{string.Join('|', announced)})$\"");
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
