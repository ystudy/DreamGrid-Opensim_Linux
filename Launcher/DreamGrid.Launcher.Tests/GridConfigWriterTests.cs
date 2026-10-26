using System.Net;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Ini;
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
        return paths;
    }

    private static LauncherSettings Make(
        string physics = "ubODE", bool owner = false, string regionName = "Welcome Area",
        List<IniOverride>? overrides = null) => new()
    {
        Hostname = "192.168.1.50",
        Db = new DatabaseSecrets("rootpw", "GENROBUSTPW", "GENOPENSIMPW"),
        Console = new ConsoleSecrets("conuser", "conpass32chars"),
        MachineId = "machine-1",
        Region = new RegionSettings("Region1", regionName, Guid.Parse("11111111-2222-3333-4444-555555555555"), 1000, 1001, physics),
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
        Write(Make() with { Region = null });
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
}
