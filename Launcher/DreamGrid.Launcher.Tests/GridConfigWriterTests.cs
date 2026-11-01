using System.Net;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Ini;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class GridConfigWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-cfg-" + Guid.NewGuid().ToString("N"));
    private LauncherPaths _paths;

    public GridConfigWriterTests() => _paths = MakePaths("data");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string RepoBin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DreamGrid.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("DreamGrid.sln not found"), "bin");
    }

    private LauncherPaths MakePaths(string dataName)
    {
        var paths = new LauncherPaths(Path.Combine(_root, dataName), Path.Combine(_root, "program"), null);
        var bin = RepoBin();
        var rt = paths.RuntimeOpenSim;
        Directory.CreateDirectory(Path.Combine(rt, "config-include", "Proto", "Robust"));
        File.Copy(Path.Combine(bin, "Robust.HG.ini.proto"), Path.Combine(rt, "Robust.HG.ini.proto"));
        File.Copy(Path.Combine(bin, "config-include", "FlotsamCache.ini.example"),
            Path.Combine(rt, "config-include", "FlotsamCache.ini.example"));
        foreach (var f in new[] { "GridCommon.ini", "OpenSim.ini" })
            File.Copy(Path.Combine(bin, "config-include", "Proto", "Robust", f),
                Path.Combine(rt, "config-include", "Proto", "Robust", f));
        // A Connected Server joined to a preset grid reads that grid's own prototypes.
        var osgrid = Path.Combine(rt, "config-include", "Proto", "OsGrid");
        Directory.CreateDirectory(osgrid);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(bin, "config-include", "Proto", "OsGrid")))
            File.Copy(f, Path.Combine(osgrid, Path.GetFileName(f)));
        return paths;
    }

    private static LauncherSettings Make(
        string physics = "ubODE", bool owner = false, string regionName = "Welcome Area",
        List<IniOverride>? overrides = null, OwnerPlan? plan = null, bool hypergrid = false,
        string hostname = "192.168.1.50") => new()
    {
        Hostname = hostname,
        Plan = plan,
        Hypergrid = hypergrid,
        Db = new DatabaseSecrets("rootpw", "GENROBUSTPW", "GENOPENSIMPW"),
        Console = new ConsoleSecrets("conuser", "conpass32chars"),
        MachineId = "machine-1",
        Programs = TestSettings.ProgramFor(new RegionSettings("Region1", regionName, Guid.Empty), physics),
        Regions = [new RegionSettings("Region1", regionName, Guid.Parse("11111111-2222-3333-4444-555555555555"), 1000, 1001, UdpPort: 8004)],
        Setup = owner
            ? new SetupState(OwnerCreated: true, Owner: new OwnerInfo("Ada", "Lovelace", Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
            : new SetupState(),
        Overrides = overrides ?? new(),
    };

    private void Write(LauncherSettings s, params IIniContributor[] c) => new GridConfigWriter(_paths).Write(s, c);

    private IniDocument Robust() => IniDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini"));
    private IniDocument Region() => IniDocument.Load(Path.Combine(_paths.RegionDir("Region1"), "OpenSim.ini"));
    private IniDocument Grid() => IniDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "config-include", "GridCommon.ini"));
    private IniDocument Flotsam() => IniDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "config-include", "FlotsamCache.ini"));

    [Fact]
    public void Hypergrid_off_by_default_uses_grid_architecture_and_closes_the_gatekeeper()
    {
        Write(Make());
        AssertHypergrid(false);
    }

    [Fact]
    public void Hypergrid_on_for_a_public_simple_server()
    {
        Write(Make(plan: OwnerPlan.SimplePublic, hypergrid: true, hostname: "203.0.113.7"));
        AssertHypergrid(true);
    }

    [Fact]
    public void Hypergrid_switch_ignored_on_a_lan_address()
    {
        Write(Make(plan: OwnerPlan.GridAsset, hypergrid: true, hostname: "192.168.1.50"));
        AssertHypergrid(false);
    }

    [Fact]
    public void Migrated_public_install_keeps_hypergrid_config()
    {
        Write(Make(plan: OwnerPlan.SimplePublic, hypergrid: true, hostname: "203.0.113.7"));
        var proto = IniDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "config-include", "Proto", "Robust", "OpenSim.ini"));
        Assert.Equal(proto.Get("Architecture", "Include-Architecture"), Region().Get("Architecture", "Include-Architecture"));
    }

    private void AssertHypergrid(bool on)
    {
        var arch = Region().Get("Architecture", "Include-Architecture")!;
        Assert.Contains(on ? "config-include/GridHypergrid.ini" : "config-include/Grid.ini", arch);
        Assert.Equal(on ? "true" : "false", Robust().Get("GatekeeperService", "ForeignAgentsAllowed"));
        Assert.Equal(on ? "true" : "false", Grid().Get("GridService", "AllowHypergridMapSearch"));
    }

    [Fact]
    public void Robust_ini_has_private_db_port_and_generated_password()
    {
        Write(Make());
        var conn = Robust().Get("DatabaseService", "ConnectionString")!;
        Assert.Contains("Port=3307", conn);
        Assert.Contains("GENROBUSTPW", conn);
        Assert.DoesNotContain("robustpassword", conn);
        Assert.Contains("Database=robust;", conn);

        var gc = Grid().Get("DatabaseService", "ConnectionString")!;
        Assert.Contains("Database=opensim;", gc);
        Assert.Contains("GENOPENSIMPW", gc);

        var r = Robust();
        Assert.Equal("My World", r.Get("Const", "GridName"));
        Assert.Equal("http://192.168.1.50", r.Get("Const", "BaseURL"));
        Assert.Equal("8001", r.Get("Const", "DiagnosticsPort"));
        Assert.Equal("8010", r.Get("Network", "ConsolePort"));
        Assert.Equal("http://192.168.1.50:8001/welcome", r.Get("GridInfoService", "welcome"));
    }

    [Fact]
    public void Viewer_password_link_points_to_forgot_password()
    {
        Write(Make(plan: OwnerPlan.SimplePublic, hostname: "203.0.113.7"));
        Assert.Equal("http://203.0.113.7:8001/guest/forgot-password", Robust().Get("GridInfoService", "password"));
    }

    [Fact]
    public void Allow_set_password_never_on()
    {
        Write(Make());
        Assert.Null(Robust().Get("AuthenticationService", "AllowSetPassword"));
        var text = File.ReadAllText(Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini"));
        Assert.DoesNotMatch(@"(?im)^\s*AllowSetPassword\s*=\s*true", text);
    }

    private static LauncherSettings JoinMake(OwnerPlan plan, bool programs) => Make(plan: plan, hypergrid: true, hostname: "203.0.113.7") with
    {
        RobustPrivatePort = 8003,
        Programs = programs ? [new ProgramSettings("Region1", 8004, 8011), new ProgramSettings("Region2", 8006, 8012)] : [],
        Regions = programs
            ?
            [
                new RegionSettings("Region1", "A", Guid.NewGuid(), UdpPort: 8004),
                new RegionSettings("Region1", "B", Guid.NewGuid(), 1001, 1000, UdpPort: 8005),
                new RegionSettings("Region2", "C", Guid.NewGuid(), 1002, 1000, UdpPort: 8006),
            ]
            : [],
    };

    [Fact]
    public void Robust_grid_info_publishes_the_join_keys()
    {
        Write(JoinMake(OwnerPlan.GridAsset, programs: true));
        var r = Robust();
        Assert.Equal("8003", r.Get("GridInfoService", "dreamgrid_private_port"));
        Assert.Equal("true", r.Get("GridInfoService", "dreamgrid_hypergrid"));
        Assert.Equal("8004-8006", r.Get("GridInfoService", "dreamgrid_region_ports"));
        Assert.Equal("grid-asset", r.Get("GridInfoService", "dreamgrid_plan"));
        Assert.Equal(LauncherVersion.Current, r.Get("GridInfoService", "dreamgrid_version"));
    }

    [Fact]
    public void Robust_grid_info_region_ports_are_empty_without_programs()
    {
        Write(JoinMake(OwnerPlan.GridAsset, programs: false));
        Assert.Equal("", Robust().Get("GridInfoService", "dreamgrid_region_ports") ?? "");
    }

    [Fact]
    public void Robust_grid_info_names_the_local_plan()
    {
        Write(Make(plan: OwnerPlan.Local));
        Assert.Equal("local", Robust().Get("GridInfoService", "dreamgrid_plan"));
    }

    [Fact]
    public void Region_ini_points_state_dirs_outside_runtime()
    {
        Write(Make());
        var r = Region();
        var values = new[]
        {
            r.Get("Startup", "regionload_regionsdir")!, r.Get("Startup", "PIDFile")!,
            r.Get("YEngine", "ScriptEnginesPath")!, Flotsam().Get("AssetCache", "CacheDirectory")!,
        };
        var data = _paths.Data.Replace('\\', '/');
        foreach (var v in values)
        {
            Assert.StartsWith(data, v);
            Assert.DoesNotContain("runtime", v.Substring(data.Length));
            Assert.DoesNotContain('\\', v);
        }
        Assert.EndsWith("/", values[0]);
    }

    [Fact]
    public void FsAssets_directories_set_in_assets_section_and_bakes_left_alone()
    {
        Write(Make());
        var r = Robust();
        Assert.Equal(_paths.FsAssetsData.Replace('\\', '/'), r.Get("AssetService", "BaseDirectory"));
        Assert.Equal(_paths.FsAssetsSpool.Replace('\\', '/'), r.Get("AssetService", "SpoolDirectory"));
        Assert.Equal("./bakes", r.Get("BakedTextureService", "BaseDirectory"));
    }

    [Theory]
    [InlineData("BulletSim", "Meshmerizer")]
    [InlineData("ubODE", "ubODEMeshmerizer")]
    public void BulletSim_gets_Meshmerizer_and_ubODE_gets_ubODEMeshmerizer(string physics, string meshing)
    {
        Write(Make(physics));
        var r = Region();
        Assert.Equal(physics, r.Get("Startup", "physics"));
        Assert.Equal(meshing, r.Get("Startup", "meshing"));
    }

    [Fact]
    public void No_active_third_party_urls_in_any_generated_file()
    {
        Write(Make(owner: true));
        var docs = new[] { Robust(), Region(), Grid(), Flotsam(),
            IniDocument.Load(Path.Combine(_paths.RegionDefinitionDir("Region1"), "Welcome Area.ini")) };
        foreach (var doc in docs)
            foreach (var (section, key, value) in doc.ActiveEntries())
            {
                foreach (var bad in new[] { "outworldz.com", "hyperica.com", "gloebit.com" })
                    Assert.False(value.Contains(bad, StringComparison.OrdinalIgnoreCase), $"[{section}] {key}={value}");
                if (key.Equals("economymodule", StringComparison.OrdinalIgnoreCase))
                    Assert.Equal("", value);
            }
        // the prototype on disk is untouched
        Assert.Contains("outworldz.com", File.ReadAllText(Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini.proto")));
    }

    /// <summary>
    /// Nini rejects any line that is not blank, a comment, a section or key=value ("Expected assignment operator").
    /// Robust.HG.ini.proto starts with a stray "3; *", which stopped Robust from booting (found in Task 10).
    /// </summary>
    [Fact]
    public void Generated_files_contain_only_lines_Nini_can_parse()
    {
        Write(Make(owner: true));
        var files = new[]
        {
            Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini"),
            Path.Combine(_paths.RuntimeOpenSim, "config-include", "GridCommon.ini"),
            Path.Combine(_paths.RuntimeOpenSim, "config-include", "FlotsamCache.ini"),
            Path.Combine(_paths.RegionDir("Region1"), "OpenSim.ini"),
            Path.Combine(_paths.RegionDefinitionDir("Region1"), "Welcome Area.ini"),
        };
        foreach (var file in files)
            foreach (var (line, n) in File.ReadAllLines(file).Select((l, i) => (l.Trim(), i + 1)))
                Assert.True(line.Length == 0 || line[0] is ';' or '#' || (line[0] == '[' && line.IndexOf(']') > 0) || line.IndexOf('=') > 0,
                    $"{Path.GetFileName(file)}:{n}: {line}");
    }

    /// <summary>
    /// Robust reads [Network] ConsolePort (HttpServerBase) but the region reads [Network] console_port
    /// (OpenSim.cs); the proto's console_port=${Const|http_listener_port} put the region console on the region
    /// HTTP port (found in Task 10).
    /// </summary>
    [Fact]
    public void Region_console_listens_on_console_port_key()
    {
        Write(Make());
        Assert.Equal("8011", Region().Get("Network", "console_port"));
        Assert.Equal("conuser", Region().Get("Network", "ConsoleUser"));
        Assert.Equal("conpass32chars", Region().Get("Network", "ConsolePass"));
        Assert.Equal("8010", Robust().Get("Network", "ConsolePort"));
    }

    [Fact]
    public void SmartStart_disabled_and_Wifi_disabled_in_both()
    {
        Write(Make());
        foreach (var doc in new[] { Robust(), Region() })
        {
            Assert.Equal("False", doc.Get("SmartStart", "Enabled"));
            Assert.Equal("http://127.0.0.1:8001", doc.Get("SmartStart", "URL"));
            Assert.Equal("machine-1", doc.Get("SmartStart", "MachineID"));
            Assert.Equal("false", doc.Get("WifiService", "Enabled"));
        }
    }

    [Fact]
    public void Region_definition_file_named_after_region_with_matching_section()
    {
        Write(Make());
        var path = Path.Combine(_paths.RegionDefinitionDir("Region1"), "Welcome Area.ini");
        Assert.True(File.Exists(path));
        var d = IniDocument.Load(path);
        Assert.Equal("11111111-2222-3333-4444-555555555555", d.Get("Welcome Area", "RegionUUID"));
        Assert.Equal("1000,1001", d.Get("Welcome Area", "Location"));
        Assert.Equal("8004", d.Get("Welcome Area", "InternalPort"));
        Assert.Equal("192.168.1.50", d.Get("Welcome Area", "ExternalHostName"));
        Assert.Equal("256", d.Get("Welcome Area", "SizeX"));
        Assert.All(d.ActiveEntries(), e => Assert.Equal("Welcome Area", e.Section));
    }

    [Fact]
    public void No_region_means_no_region_files()
    {
        Write(Make() with { Programs = [], Regions = [] });
        Assert.True(File.Exists(Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini")));
        Assert.False(Directory.Exists(_paths.RegionsRoot));
    }

    [Fact]
    public void Estates_keys_present_only_after_owner_created()
    {
        Write(Make());
        Assert.True(string.IsNullOrWhiteSpace(Region().Get("Estates", "DefaultEstateOwnerName")));
        Assert.True(string.IsNullOrWhiteSpace(Region().Get("Estates", "DefaultEstateOwnerUUID")));

        Write(Make(owner: true));
        var r = Region();
        Assert.Equal("My World Estate", r.Get("Estates", "DefaultEstateName"));
        Assert.Equal("Ada Lovelace", r.Get("Estates", "DefaultEstateOwnerName"));
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", r.Get("Estates", "DefaultEstateOwnerUUID"));
    }

    private sealed class Contributor : IIniContributor
    {
        public IEnumerable<IniOverride> Contribute(LauncherSettings s) =>
            [new IniOverride("robust", "Hypergrid", "Foo", "bar"), new IniOverride("region", "Startup", "physics", "Contributed")];
    }

    [Fact]
    public void Overrides_and_contributors_win_over_generated_values()
    {
        Write(Make(overrides: [new IniOverride("region", "Startup", "physics", "BulletSim"),
                               new IniOverride("robust", "Const", "GridName", "Overridden")]));
        Assert.Equal("BulletSim", Region().Get("Startup", "physics"));
        Assert.Equal("Overridden", Robust().Get("Const", "GridName"));

        Write(Make(), new Contributor());
        Assert.Equal("Contributed", Region().Get("Startup", "physics"));
        Assert.Equal("bar", Robust().Get("Hypergrid", "Foo"));
        Assert.Null(Region().Get("Hypergrid", "Foo"));
    }

    [Fact]
    public void Paths_with_spaces_and_non_ascii_survive()
    {
        _paths = MakePaths("Dréam Grid");
        Write(Make());
        var expected = _paths.AssetCache.Replace('\\', '/');
        Assert.Contains("Dréam Grid", expected);
        Assert.Equal(expected, Flotsam().Get("AssetCache", "CacheDirectory"));
        Assert.StartsWith(_paths.Data.Replace('\\', '/'), Region().Get("Startup", "PIDFile"));
    }

    [Fact]
    public void HostAddress_detects_a_parseable_IPv4_address()
    {
        var ip = HostAddress.Detect();
        Assert.True(IPAddress.TryParse(ip, out var parsed));
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, parsed!.AddressFamily);
    }

    [Fact]
    public void Writer_omits_estate_owner_keys_without_owner()
    {
        // A prototype that carries active owner keys must not leak them into a grid with no owner yet.
        var proto = Path.Combine(_paths.RuntimeOpenSim, "config-include", "Proto", "Robust", "OpenSim.ini");
        File.AppendAllText(proto, "\n[Estates]\nDefaultEstateOwnerName=Old Owner\nDefaultEstateOwnerUUID=00000000-0000-0000-0000-000000000001\n");

        Write(Make(owner: false));

        var active = Region().ActiveEntries().Where(e => e.Section == "Estates").Select(e => e.Key).ToList();
        Assert.DoesNotContain("DefaultEstateOwnerName", active);
        Assert.DoesNotContain("DefaultEstateOwnerUUID", active);
    }

    [Fact]
    public void Effective_whitespace_hostname_falls_back_to_detected() =>
        Assert.Equal(HostAddress.Detect(), HostAddress.Effective("   "));

    [Fact]
    public void Effective_trims_valid_hostname() =>
        Assert.Equal("host.lan", HostAddress.Effective(" host.lan "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a host")]
    public void Effective_falls_back_to_detected_for_missing_or_invalid(string? configured) =>
        Assert.Equal(HostAddress.Detect(), HostAddress.Effective(configured));

    // ---- programs and regions (plan 4a) ----

    private static readonly Guid HomeId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("10000000-0000-0000-0000-000000000002");

    /// <summary>Home and Beach sharing program "Home" (8004/8011), Isle on its own (8006/8012).</summary>
    private static LauncherSettings TwoPrograms(bool beachParked = false, bool beachDeleted = false) => Make() with
    {
        Programs = [new ProgramSettings("Home", 8004, 8011), new ProgramSettings("Isle", 8006, 8012, "BulletSim")],
        Regions =
        [
            new RegionSettings("Home", "Home", HomeId, 1000, 1000, UdpPort: 8004),
            new RegionSettings("Home", "Beach", BeachId, 1001, 1000, UdpPort: 8005, Parked: beachParked,
                DeletedAt: beachDeleted ? DateTimeOffset.UnixEpoch : null),
            new RegionSettings("Isle", "Isle", Guid.NewGuid(), 1010, 1010, Size: 4, UdpPort: 8006),
        ],
    };

    private IniDocument ProgramIni(string instance) => IniDocument.Load(Path.Combine(_paths.RegionDir(instance), "OpenSim.ini"));

    private string RegionFile(string instance, string name) => Path.Combine(_paths.RegionDefinitionDir(instance), name + ".ini");

    [Fact]
    public void Writes_one_opensim_ini_per_program_with_its_ports()
    {
        Write(TwoPrograms());

        Assert.Equal("8004", ProgramIni("Home").Get("Const", "http_listener_port"));
        Assert.Equal("8011", ProgramIni("Home").Get("Network", "console_port"));
        Assert.Equal("8006", ProgramIni("Isle").Get("Const", "http_listener_port"));
        Assert.Equal("8012", ProgramIni("Isle").Get("Network", "console_port"));
        Assert.Equal("BulletSim", ProgramIni("Isle").Get("Startup", "physics"));
        Assert.EndsWith("/Isle.pid", ProgramIni("Isle").Get("Startup", "PIDFile"));
    }

    [Fact]
    public void Shared_program_gets_one_region_file_per_region()
    {
        Write(TwoPrograms());

        Assert.Equal("8004", IniDocument.Load(RegionFile("Home", "Home")).Get("Home", "InternalPort"));
        Assert.Equal("8005", IniDocument.Load(RegionFile("Home", "Beach")).Get("Beach", "InternalPort"));
        Assert.Equal("1001,1000", IniDocument.Load(RegionFile("Home", "Beach")).Get("Beach", "Location"));
    }

    [Fact]
    public void Region_size_sets_SizeX_and_SizeY()
    {
        Write(TwoPrograms());

        var isle = IniDocument.Load(RegionFile("Isle", "Isle"));
        Assert.Equal("1024", isle.Get("Isle", "SizeX"));
        Assert.Equal("1024", isle.Get("Isle", "SizeY"));
    }

    [Fact]
    public void Parked_held_and_deleted_regions_are_set_aside()
    {
        Write(TwoPrograms());
        Assert.True(File.Exists(RegionFile("Home", "Beach")));

        foreach (var (settings, held) in new[]
                 {
                     (TwoPrograms(beachParked: true), (IReadOnlySet<Guid>?)null),
                     (TwoPrograms(), new HashSet<Guid> { BeachId }),
                     (TwoPrograms(beachDeleted: true), null),
                 })
        {
            Write(TwoPrograms());
            new GridConfigWriter(_paths).Write(settings, [], held);

            Assert.False(File.Exists(RegionFile("Home", "Beach")));
            Assert.True(File.Exists(RegionFile("Home", "Beach") + ".off"));
            Assert.True(File.Exists(RegionFile("Home", "Home")));
        }
    }

    [Fact]
    public void Case_only_rename_leaves_one_region_file_in_the_new_case()
    {
        // The region was "home" before a rename to "Home": its old file is still there.
        var dir = _paths.RegionDefinitionDir("Home");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "home.ini"), "[home]\nRegionUUID = " + HomeId + "\n");

        Write(TwoPrograms());

        var active = Directory.EnumerateFiles(dir).Select(Path.GetFileName)
            .Where(n => n!.EndsWith(".ini", StringComparison.Ordinal)
                        && n.Equals("Home.ini", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(["Home.ini"], active);
        Assert.Equal("8004", IniDocument.Load(RegionFile("Home", "Home")).Get("Home", "InternalPort"));
    }

    // ---- region and program options (plan 4d) ----

    private static LauncherSettings WithOptions(ProgramOptions? po = null, string? physics = null, string? defaultPhysics = null,
        params (string Name, RegionOptions Opt)[] regions)
    {
        var list = regions.Length == 0 ? [("Home", new RegionOptions())] : regions;
        return Make() with
        {
            DefaultPhysics = defaultPhysics ?? "ubODE",
            Programs = [new ProgramSettings("Home", 8004, 8011, physics, po)],
            Regions = list.Select((r, i) => new RegionSettings("Home", r.Name, Guid.NewGuid(), 1000 + i, 1000,
                UdpPort: 8004 + i, Options: r.Opt)).ToList(),
        };
    }

    [Fact]
    public void Region_file_gets_only_the_options_that_are_set()
    {
        Write(WithOptions(regions: [("Home", new RegionOptions(MaxAgents: 20, MaxPrims: 5000, MaxNonPhysicalSize: 128, MaxPhysicalSize: 64, ClampSize: true)),
                                    ("Beach", new RegionOptions(MaxAgents: 5))]));

        var home = IniDocument.Load(RegionFile("Home", "Home"));
        Assert.Equal("20", home.Get("Home", "MaxAgents"));
        Assert.Equal("5000", home.Get("Home", "MaxPrims"));
        Assert.Equal("128", home.Get("Home", "NonPhysicalPrimMax"));
        Assert.Equal("64", home.Get("Home", "PhysicalPrimMax"));
        Assert.Equal("True", home.Get("Home", "ClampPrimSize"));

        var beach = IniDocument.Load(RegionFile("Home", "Beach"));
        Assert.Equal("5", beach.Get("Beach", "MaxAgents"));
        foreach (var k in new[] { "MaxPrims", "NonPhysicalPrimMax", "PhysicalPrimMax", "ClampPrimSize" })
            Assert.Null(beach.Get("Beach", k));
    }

    [Theory]
    [InlineData("ubODE", null, "ubODE", "ubODEMeshmerizer", null)]
    [InlineData("BulletSim", null, "BulletSim", "Meshmerizer", "false")]
    [InlineData("BulletSimThreaded", null, "BulletSim", "Meshmerizer", "true")]
    [InlineData(null, "BulletSimThreaded", "BulletSim", "Meshmerizer", "true")]
    public void Physics_rows_match_the_table(string? own, string? def, string physics, string meshing, string? separate)
    {
        Write(WithOptions(physics: own, defaultPhysics: def));

        var os = ProgramIni("Home");
        Assert.Equal(physics, os.Get("Startup", "physics"));
        Assert.Equal(meshing, os.Get("Startup", "meshing"));
        if (separate is not null) Assert.Equal(separate, os.Get("BulletSim", "UseSeparatePhysicsThread"));
    }

    [Fact]
    public void Scripts_off_disables_yengine()
    {
        Write(WithOptions(new ProgramOptions(Scripts: false)));
        Assert.Equal("false", ProgramIni("Home").Get("YEngine", "Enabled"));

        Write(WithOptions(new ProgramOptions(Scripts: true)));
        Assert.Equal("true", ProgramIni("Home").Get("YEngine", "Enabled"));

        Write(WithOptions() with { DefaultScripts = false });
        Assert.Equal("false", ProgramIni("Home").Get("YEngine", "Enabled"));
    }

    [Fact]
    public void Frame_rate_writes_frame_time()
    {
        Write(WithOptions(new ProgramOptions(MinTimerInterval: 0.2, FramesPerSecond: 20)));
        var os = ProgramIni("Home");
        Assert.Equal("0.2", os.Get("YEngine", "MinTimerInterval"));
        Assert.Equal("0.0500", os.Get("Startup", "FrameTime"));
    }

    [Fact]
    public void Numbers_use_the_invariant_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Write(WithOptions(new ProgramOptions(MinTimerInterval: 0.25, FramesPerSecond: 40)));
            Assert.Equal("0.0250", ProgramIni("Home").Get("Startup", "FrameTime"));
            Assert.Equal("0.25", ProgramIni("Home").Get("YEngine", "MinTimerInterval"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Theory]
    [InlineData(MapQuality.None)]
    [InlineData(MapQuality.Simple)]
    [InlineData(MapQuality.Good)]
    [InlineData(MapQuality.Better)]
    [InlineData(MapQuality.Best)]
    public void Map_levels_write_every_map_key(MapQuality q)
    {
        Write(WithOptions(new ProgramOptions(Map: q)));

        var os = ProgramIni("Home");
        Assert.Equal(q != MapQuality.None ? "true" : "false", os.Get("Map", "GenerateMaptiles"));
        if (q != MapQuality.None)
            Assert.Equal(q == MapQuality.Simple ? "MapImageModule" : "Warp3DImageModule", os.Get("Map", "MapImageModule"));
        Assert.Equal(q >= MapQuality.Better ? "true" : "false", os.Get("Map", "TextureOnMapTile"));
        Assert.Equal(q >= MapQuality.Better ? "true" : "false", os.Get("Map", "DrawPrimOnMapTile"));
        Assert.Equal(q == MapQuality.Best ? "true" : "false", os.Get("Map", "TexturePrims"));
        Assert.Equal(q == MapQuality.Best ? "true" : "false", os.Get("Map", "RenderMeshes"));
    }

    [Fact]
    public void Permissions_are_written()
    {
        Write(WithOptions(new ProgramOptions(AllowGods: false, OwnerIsGod: false, ManagerIsGod: true)));

        var os = ProgramIni("Home");
        Assert.Equal("false", os.Get("Permissions", "allow_grid_gods"));
        Assert.Equal("false", os.Get("Permissions", "region_owner_is_god"));
        Assert.Equal("true", os.Get("Permissions", "region_manager_is_god"));
    }

    [Fact]
    public void Access_keys_use_underscored_names()
    {
        Write(WithOptions(regions: [("My Place", new RegionOptions(KeepResidentsOut: true)), ("Open", new RegionOptions())]));

        var os = ProgramIni("Home");
        Assert.Equal("DisallowResidents", os.Get("AuthorizationService", "Region_My_Place"));
        Assert.Null(os.Get("AuthorizationService", "Region_Open"));
    }

    [Fact]
    public void Concierge_regex_escapes_names()
    {
        Write(WithOptions(regions: [("Joe's (Beach)", new RegionOptions(AnnounceVisitors: true)),
                                    ("Home", new RegionOptions(AnnounceVisitors: true)),
                                    ("Quiet", new RegionOptions())]));

        var os = ProgramIni("Home");
        Assert.Equal("true", os.Get("Concierge", "enabled"));
        var value = os.Get("Concierge", "regions")!;
        Assert.Equal("^(?:Joe's\\ \\(Beach\\)|Home)$", value);
        // On disk the value stays quoted with its backslashes as written.
        Assert.Contains("regions = \"^(?:Joe's\\ \\(Beach\\)|Home)$\"",
            File.ReadAllText(Path.Combine(_paths.RegionDir("Home"), "OpenSim.ini")).Replace("regions=", "regions = "));
        var re = new System.Text.RegularExpressions.Regex(value,System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.Matches(re, "Joe's (Beach)");
        Assert.Matches(re, "Home");
        Assert.DoesNotMatch(re, "Homestead");
        Assert.DoesNotMatch(re, "Quiet");
    }

    [Fact]
    public void Concierge_is_off_when_nobody_announces()
    {
        Write(WithOptions());
        Assert.Equal("false", ProgramIni("Home").Get("Concierge", "enabled"));
    }

    [Fact]
    public void Programs_get_only_their_own_regions_access_keys()
    {
        var s = TwoPrograms() with
        {
            Regions =
            [
                new RegionSettings("Home", "Home", HomeId, 1000, 1000, UdpPort: 8004, Options: new RegionOptions(KeepResidentsOut: true)),
                new RegionSettings("Isle", "Isle", Guid.NewGuid(), 1010, 1010, UdpPort: 8006, Options: new RegionOptions(AnnounceVisitors: true)),
            ],
        };
        Write(s);

        Assert.Equal("DisallowResidents", ProgramIni("Home").Get("AuthorizationService", "Region_Home"));
        Assert.Null(ProgramIni("Isle").Get("AuthorizationService", "Region_Home"));
        Assert.Null(ProgramIni("Home").Get("AuthorizationService", "Region_Isle"));
        Assert.Equal("false", ProgramIni("Home").Get("Concierge", "enabled"));
        Assert.Equal("true", ProgramIni("Isle").Get("Concierge", "enabled"));
    }

    [Fact]
    public void Default_options_write_the_same_region_files_as_before()
    {
        Write(WithOptions());

        var keys = IniDocument.Load(RegionFile("Home", "Home")).ActiveEntries().Select(e => e.Key).ToList();
        Assert.Equal(["RegionUUID", "Location", "InternalAddress", "InternalPort", "ExternalHostName", "SizeX", "SizeY"], keys);
        var os = ProgramIni("Home");
        Assert.Equal("0.091", os.Get("Startup", "FrameTime"));
        Assert.Null(os.Get("YEngine", "MinTimerInterval"));
        Assert.Null(os.Get("AuthorizationService", "Region_Home"));
    }

    private static LauncherSettings RegionServer(bool hypergrid) => Make(plan: OwnerPlan.RegionServer, owner: true, hostname: "192.168.1.20") with
    {
        Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "7.1.0", hypergrid, 8004, 8006),
        Setup = new SetupState(OwnerCreated: true, Owner: new OwnerInfo("Ann", "Lee", Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"))),
    };

    [Fact]
    public void Region_server_points_regions_at_the_grid_asset_server()
    {
        // The owner typed the Grid Asset Server's LAN address; the grid publishes its own public name as its login.
        var s = RegionServer(hypergrid: false);
        Write(s with { Join = s.Join! with { LoginUri = "http://grid.example.org:8002/" } });

        var r = Region();
        Assert.Equal("192.168.1.20", r.Get("Const", "BaseHostname"));
        Assert.Equal("http://grid.example.org", r.Get("Const", "BaseURL"));
        Assert.Equal("http://192.168.1.5", r.Get("Const", "PrivURL"));
        Assert.Equal("8002", r.Get("Const", "PublicPort"));
        Assert.Equal("8003", r.Get("Const", "PrivatePort"));
        Assert.Equal("config-include/Grid.ini", r.Get("Architecture", "Include-Architecture"));
        Assert.Equal("Ann Lee", r.Get("Estates", "DefaultEstateOwnerName"));
        Assert.True(string.IsNullOrWhiteSpace(r.Get("Estates", "DefaultEstateOwnerUUID")));
        Assert.Equal("false", Grid().Get("GridService", "AllowHypergridMapSearch"));
        Assert.Equal("192.168.1.20", IniDocument.Load(RegionFile("Region1", "Welcome Area")).Get("Welcome Area", "ExternalHostName"));
    }

    [Fact]
    public void Region_server_advertises_the_port_of_the_grids_login_address()
    {
        var s = RegionServer(hypergrid: false);
        Write(s with { Join = s.Join! with { LoginUri = "https://grid.example.org:9000/" } });

        var r = Region();
        Assert.Equal("https://grid.example.org", r.Get("Const", "BaseURL"));
        Assert.Equal("9000", r.Get("Const", "PublicPort"));
        Assert.Equal("http://192.168.1.5", r.Get("Const", "PrivURL"));
        Assert.Equal("8003", r.Get("Const", "PrivatePort"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://grid.example.org:21/")]
    public void Region_server_falls_back_to_the_typed_address_when_the_login_address_cannot_be_read(string login)
    {
        var s = RegionServer(hypergrid: false);
        Write(s with { Join = s.Join! with { LoginUri = login } });

        var r = Region();
        Assert.Equal("http://192.168.1.5", r.Get("Const", "BaseURL"));
        Assert.Equal("8002", r.Get("Const", "PublicPort"));
    }

    [Fact]
    public void Region_server_with_hypergrid_on_follows_the_grid_asset_server()
    {
        var s = RegionServer(hypergrid: true);
        Assert.False(s.Hypergrid);
        Write(s);

        Assert.Equal("config-include/GridHypergrid.ini", Region().Get("Architecture", "Include-Architecture"));
        Assert.Equal("true", Grid().Get("GridService", "AllowHypergridMapSearch"));
    }

    [Fact]
    public void Region_server_without_a_join_yet_writes_the_single_machine_endpoints()
    {
        Write(RegionServer(hypergrid: false) with { Join = null });

        var r = Region();
        Assert.Equal("http://127.0.0.1", r.Get("Const", "PrivURL"));
        Assert.Equal("8002", r.Get("Const", "PublicPort"));
    }

    private static LauncherSettings Connected(ConnectedGrid grid) => Make(plan: OwnerPlan.Connected, owner: true, hostname: "203.0.113.7") with
    {
        Connected = grid,
    };

    private static readonly ConnectedGrid Elsewhere = new(null, "Elsewhere", "http://10.0.0.9:8002/",
        "http://grid.example.org:8002/", "http://grid.example.org:8003/", "http://grid.example.org:8002/");

    private static readonly ConnectedGrid OsGrid = new("osgrid", "OSGrid", "http://login.osgrid.org/",
        "http://login.osgrid.org", "http://grid.osgrid.org/", "http://hg.osgrid.org/");

    [Fact]
    public void Connected_to_another_grid_uses_its_addresses()
    {
        Write(Connected(Elsewhere));

        var r = Region();
        Assert.Equal("203.0.113.7", r.Get("Const", "BaseHostname"));
        Assert.Equal("http://grid.example.org", r.Get("Const", "BaseURL"));
        Assert.Equal("8002", r.Get("Const", "PublicPort"));
        Assert.Equal("http://grid.example.org", r.Get("Const", "PrivURL"));
        Assert.Equal("8003", r.Get("Const", "PrivatePort"));
        Assert.Equal("config-include/GridHypergrid.ini", r.Get("Architecture", "Include-Architecture"));
        Assert.Equal("Ada Lovelace", r.Get("Estates", "DefaultEstateOwnerName"));
        Assert.True(string.IsNullOrWhiteSpace(r.Get("Estates", "DefaultEstateOwnerUUID")));
        Assert.Equal("${Const|PrivURL}:${Const|PrivatePort}", Grid().Get("AssetService", "AssetServerURI"));
        // The gatekeeper is the grid's own login address, so the hypergrid keys keep building it from [Const].
        Assert.Equal("${Const|BaseURL}:${Const|PublicPort}", Grid().Get("Hypergrid", "GatekeeperURI"));
        Assert.Equal("203.0.113.7", IniDocument.Load(RegionFile("Region1", "Welcome Area")).Get("Welcome Area", "ExternalHostName"));
    }

    [Fact]
    public void Connected_to_another_grid_without_gatekeeper_uses_grid_ini()
    {
        Write(Connected(Elsewhere with { GatekeeperUri = null }));

        Assert.Equal("config-include/Grid.ini", Region().Get("Architecture", "Include-Architecture"));
        Assert.Equal("false", Grid().Get("GridService", "AllowHypergridMapSearch"));
    }

    [Fact]
    public void Connected_to_another_grid_with_its_own_gatekeeper_points_hypergrid_there()
    {
        Write(Connected(Elsewhere with { GatekeeperUri = "http://hg.example.org:8002/" }));

        var g = Grid();
        foreach (var (section, key) in new[]
                 {
                     ("Hypergrid", "HomeURI"), ("Hypergrid", "GatekeeperURI"), ("GridService", "Gatekeeper"), ("Messaging", "Gatekeeper"),
                     ("HGInventoryAccessModule", "HomeURI"), ("HGInventoryAccessModule", "Gatekeeper"), ("HGAssetService", "HomeURI"),
                     ("UserAgentService", "UserAgentServerURI"),
                 })
            Assert.Equal("http://hg.example.org:8002", g.Get(section, key));
    }

    [Fact]
    public void Connected_to_osgrid_uses_the_osgrid_proto()
    {
        var s = Connected(OsGrid);
        Write(s);

        var g = Grid();
        Assert.Equal("http://assets.osgrid.org", g.Get("AssetService", "AssetServerURI"));
        Assert.Equal("http://users.osgrid.org", g.Get("UserAccountService", "UserAccountServerURI"));
        Assert.Equal("http://hg.osgrid.org:80", g.Get("Hypergrid", "GatekeeperURI"));
        var conn = g.Get("DatabaseService", "ConnectionString")!;
        Assert.Contains("Database=opensim;", conn);
        Assert.Contains($"Port={s.MariaDbPort};", conn);

        var r = Region();
        Assert.Equal("203.0.113.7", r.Get("Const", "BaseHostname"));
        Assert.Equal("http://login.osgrid.org", r.Get("Const", "BaseURL"));
        Assert.Equal("80", r.Get("Const", "PublicPort"));
        Assert.Equal("http://groups.osgrid.org/xmlrpc.php", r.Get("Groups", "GroupsServerURI"));
        Assert.Equal("XmlRpcGroupsServicesConnector", r.Get("Groups", "ServicesConnectorModule"));
        Assert.Null(r.Get("Groups", "LocalService"));
        Assert.Equal("http://services.osgrid.org", r.Get("UserProfiles", "ProfileServiceURL"));
        Assert.Equal("http://xbakes.osgrid.org", r.Get("XBakes", "URL"));
        Assert.Equal("http://services.osgrid.org", r.Get("Messaging", "OfflineMessageURL"));
        Assert.Equal("MuteListModule", r.Get("Messaging", "MuteListModule"));
        Assert.Equal("http://services.osgrid.org", r.Get("Messaging", "MuteListURL"));
        // Textures are served by the region itself, as OSgrid's own OpenSim.ini says, not by OSgrid's login address.
        Assert.Equal("localhost", r.Get("ClientStack.LindenCaps", "Cap_GetTexture"));
        Assert.Equal("http://maptiles.osgrid.org:80", r.Get("SimulatorFeatures", "MapTileURL"));
        Assert.Equal("http://search.osgrid.org/v2/query.php", r.Get("Search", "SearchURL"));
    }

    public static TheoryData<string, OwnerPlan, string, bool> SnapshotCases => new()
    {
        { "local", OwnerPlan.Local, "192.168.1.50", false },
        { "simple-public", OwnerPlan.SimplePublic, "203.0.113.7", true },
        { "grid-asset", OwnerPlan.GridAsset, "203.0.113.7", true },
    };

    /// <summary>
    /// The INIs the single-machine plans wrote before the Region Server existed, captured from the writer then. Set
    /// DG_CAPTURE_SNAPSHOTS=1 to rewrite them (only when a change to these INIs is intended).
    /// </summary>
    [Theory]
    [MemberData(nameof(SnapshotCases))]
    public void Single_machine_plans_write_what_they_did_before(string name, OwnerPlan plan, string host, bool hypergrid)
    {
        Write(Make(plan: plan, hypergrid: hypergrid, hostname: host, owner: true));

        var files = new Dictionary<string, string>
        {
            ["OpenSim.ini"] = Path.Combine(_paths.RegionDir("Region1"), "OpenSim.ini"),
            ["GridCommon.ini"] = Path.Combine(_paths.RuntimeOpenSim, "config-include", "GridCommon.ini"),
            ["Region.ini"] = RegionFile("Region1", "Welcome Area"),
        };
        var dir = Path.Combine(Path.GetDirectoryName(RepoBin())!, "Launcher", "DreamGrid.Launcher.Tests", "Snapshots");
        foreach (var (file, path) in files)
        {
            var actual = Normalize(File.ReadAllText(path)).Replace(_root, "<ROOT>").Replace(Normalize(_root, true), "<ROOT>");
            var snapshot = Path.Combine(dir, $"{name}.{file}");
            if (Environment.GetEnvironmentVariable("DG_CAPTURE_SNAPSHOTS") == "1")
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(snapshot, actual);
            }
            Assert.Equal(Normalize(File.ReadAllText(snapshot)), actual);
        }
    }

    private static string Normalize(string text, bool slashes = false) =>
        (slashes ? text.Replace(Path.DirectorySeparatorChar, (char)47) : text).ReplaceLineEndings(((char)10).ToString());
}
