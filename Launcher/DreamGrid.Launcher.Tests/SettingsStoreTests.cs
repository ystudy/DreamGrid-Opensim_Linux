using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dgtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, true); } catch { }
    }

    [Fact]
    public void Settings_tmp_left_by_a_crash_is_ignored_and_removed()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var good = new SettingsStore(path).LoadOrCreate();
        File.WriteAllText(path + ".tmp", "{\"webPort\": 80");

        var loaded = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(good.Db.RobustPassword, loaded.Db.RobustPassword);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_removes_tmp_when_replace_fails()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var settings = new SettingsStore(Path.Combine(NewDir(), "x.json")).LoadOrCreate();
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<Exception>(() => new SettingsStore(path).Save(settings));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_does_not_retry_non_sharing_errors()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var settings = new SettingsStore(Path.Combine(NewDir(), "x.json")).LoadOrCreate();
        Directory.CreateDirectory(path);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => new SettingsStore(path).Save(settings));
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Load_rejects_null_for_required_member()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["db"] = null;
        File.WriteAllText(path, node.ToJsonString());

        Assert.Throws<SettingsCorruptException>(() => new SettingsStore(path).LoadOrCreate());
    }

    [Fact]
    public void LoadOrCreate_creates_file_with_unique_random_secrets()
    {
        var a = new SettingsStore(Path.Combine(NewDir(), "settings.json")).LoadOrCreate();
        var b = new SettingsStore(Path.Combine(NewDir(), "settings.json")).LoadOrCreate();

        Assert.NotEqual(a.Db.RobustPassword, b.Db.RobustPassword);
        Assert.Equal(32, a.Db.RobustPassword.Length);
        Assert.Matches(new Regex("^[A-Za-z0-9]{32}$"), a.Db.RobustPassword);
        Assert.NotNull(a.SetupToken);
    }

    [Fact]
    public void LoadOrCreate_returns_existing_values_unchanged()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        var created = store.LoadOrCreate();
        store.Save(created with { GridName = "Avalon" });

        var loaded = new SettingsStore(path).LoadOrCreate();

        Assert.Equal("Avalon", loaded.GridName);
        Assert.Equal(created.Db, loaded.Db);
        Assert.Equal(created.Console, loaded.Console);
        Assert.Equal(created.MachineId, loaded.MachineId);
        Assert.Equal(created.SetupToken, loaded.SetupToken);
    }

    [Fact]
    public void Corrupt_file_throws_and_is_left_untouched()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        File.WriteAllText(path, "{\"GridName\": ");
        var before = File.ReadAllBytes(path);

        var ex = Assert.Throws<SettingsCorruptException>(() => new SettingsStore(path).LoadOrCreate());

        Assert.Contains(path, ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Concurrent_reads_never_make_a_save_fail()
    {
        var store = new SettingsStore(Path.Combine(NewDir(), "settings.json"));
        var s = store.LoadOrCreate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested) { store.LoadOrCreate(); reads++; }
            return reads;
        });
        var saves = 0;
        while (!stop.IsCancellationRequested) { store.Save(s with { GridName = "G" + saves }); saves++; }

        Assert.True(await reader > 0);
        Assert.True(saves > 0);
    }

    [Fact]
    public void Save_is_atomic_leaves_no_tmp_file()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        store.Save(store.LoadOrCreate() with { GridName = "X" });

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Unix_file_mode_is_owner_only()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Save_makes_the_settings_directory_owner_only_on_linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var dir = Path.Combine(NewDir(), "data");
        new SettingsStore(Path.Combine(dir, "settings.json")).LoadOrCreate();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }

    /// <summary>A version 1 settings file: a fresh file rewritten with schemaVersion 1 and the given region object.</summary>
    private string WriteV1(string? regionJson)
    {
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["schemaVersion"] = 1;
        node.Remove("programs");
        node.Remove("regions");
        node["regionPort"] = 8004;
        node["regionConsolePort"] = 8011;
        node["region"] = regionJson is null ? null : System.Text.Json.Nodes.JsonNode.Parse(regionJson);
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    private static readonly Guid V1Uuid = Guid.Parse("6f1c1b8e-2f1a-4c55-9d3e-0a7b2c4d5e6f");

    private const string V1Region =
        "{\"instance\":\"Welcome\",\"name\":\"Welcome\",\"uuid\":\"6f1c1b8e-2f1a-4c55-9d3e-0a7b2c4d5e6f\",\"x\":1000,\"y\":1000,\"physics\":\"BulletSim\",\"content\":\"island\"}";

    private string WriteV2(string programsJson, string? regionsJson = null)
    {
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["schemaVersion"] = 2;
        node["programs"] = System.Text.Json.Nodes.JsonNode.Parse(programsJson);
        node["regions"] = System.Text.Json.Nodes.JsonNode.Parse(regionsJson ?? "[]");
        node.Remove("defaultPhysics");
        node.Remove("defaultScripts");
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    [Fact]
    public void Schema2_physics_ubODE_becomes_grid_default_and_BulletSim_keeps_its_own_thread()
    {
        var path = WriteV2("[{\"instance\":\"A\",\"httpPort\":8004,\"consolePort\":8011,\"physics\":\"ubODE\"},"
            + "{\"instance\":\"B\",\"httpPort\":8005,\"consolePort\":8012,\"physics\":\"BulletSim\"}]");

        var s = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(5, s.SchemaVersion);
        Assert.Null(s.Programs[0].Physics);
        // Schema 2's BulletSim ran with the prototype's UseSeparatePhysicsThread=true: it keeps running that way.
        Assert.Equal("BulletSimThreaded", s.Programs[1].Physics);
        Assert.Equal("ubODE", s.DefaultPhysics);
        Assert.True(s.DefaultScripts);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(5, (int)node["schemaVersion"]!);
    }

    [Fact]
    public void Schema1_file_migrates_through_to_4()
    {
        var s = new SettingsStore(WriteV1(V1Region.Replace("BulletSim", "ubODE"))).LoadOrCreate();

        Assert.Equal(5, s.SchemaVersion);
        Assert.Null(Assert.Single(s.Programs).Physics);
    }

    [Fact]
    public void Schema3_explicit_ubODE_survives_a_reload()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        store.Save(store.LoadOrCreate() with
        {
            DefaultPhysics = "BulletSim",
            Programs = [new ProgramSettings("A", 8004, 8011, "ubODE"), new ProgramSettings("B", 8005, 8012, "BulletSim")],
        });

        var loaded = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(5, loaded.SchemaVersion);
        Assert.Equal(["ubODE", "BulletSim"], loaded.Programs.Select(p => p.Physics));
    }

    [Fact]
    public void Missing_options_read_as_defaults()
    {
        var path = WriteV2("[{\"instance\":\"A\",\"httpPort\":8004,\"consolePort\":8011}]",
            "[{\"instance\":\"A\",\"name\":\"A\",\"uuid\":\"6f1c1b8e-2f1a-4c55-9d3e-0a7b2c4d5e6f\"}]");

        var s = new SettingsStore(path).LoadOrCreate();

        Assert.Null(s.Programs[0].Options);
        Assert.Null(s.Regions[0].Options);
        Assert.True(RegionSettingsRules.Options(s.Regions[0].Options ?? new RegionOptions()).Ok);
        Assert.True(RegionSettingsRules.Program(null, s.Programs[0].Options ?? new ProgramOptions(), 8).Ok);
    }

    [Fact]
    public void Options_round_trip()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        var s = store.LoadOrCreate() with
        {
            DefaultPhysics = "BulletSim",
            Programs = [new ProgramSettings("A", 8004, 8011, "BulletSimThreaded", new ProgramOptions(false, 0.5, 20, MapQuality.Best, Cores: [0, 2], Priority: CpuPriority.High))],
            Regions = [new RegionSettings("A", "A", Guid.NewGuid(), UdpPort: 8004, Options: new RegionOptions(MaxAgents: 10, ClampSize: true))],
        };
        store.Save(s);

        var loaded = new SettingsStore(path).LoadOrCreate();

        Assert.Equal("BulletSim", loaded.DefaultPhysics);
        Assert.Equal(s.Programs[0].Options!.Cores, loaded.Programs[0].Options!.Cores);
        Assert.Equal(s.Programs[0].Options! with { Cores = null }, loaded.Programs[0].Options! with { Cores = null });
        Assert.Equal(s.Regions[0].Options, loaded.Regions[0].Options);
    }

    [Fact]
    public void Migrates_v1_single_region()
    {
        var s = new SettingsStore(WriteV1(V1Region)).LoadOrCreate();

        Assert.Equal(5, s.SchemaVersion);
        Assert.Equal([new ProgramSettings("Welcome", 8004, 8011, "BulletSimThreaded")], s.Programs);
        Assert.Equal([new RegionSettings("Welcome", "Welcome", V1Uuid, 1000, 1000, "island", 1, 8004)], s.Regions);
        Assert.Null(s.LegacyRegion);
    }

    [Fact]
    public void Migration_is_saved_once()
    {
        var path = WriteV1(V1Region);
        new SettingsStore(path).LoadOrCreate();

        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(5, (int)node["schemaVersion"]!);
        Assert.True(node.ContainsKey("programs"));
        Assert.False(node.ContainsKey("region"));
    }

    [Fact]
    public void V1_without_region_migrates_to_empty_lists()
    {
        var s = new SettingsStore(WriteV1(null)).LoadOrCreate();

        Assert.Equal(5, s.SchemaVersion);
        Assert.Empty(s.Programs);
        Assert.Empty(s.Regions);
    }

    [Fact]
    public void Schema_2_round_trips()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        var deleted = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var s = store.LoadOrCreate() with
        {
            Programs = [new ProgramSettings("Home", 8004, 8011), new ProgramSettings("Isle", 8006, 8012, "BulletSim")],
            Regions =
            [
                new RegionSettings("Home", "Home", Guid.NewGuid(), UdpPort: 8004),
                new RegionSettings("Home", "Beach", Guid.NewGuid(), 1001, 1000, "island", 2, 8005, Parked: true),
                new RegionSettings("Isle", "Isle", Guid.NewGuid(), 1010, 1010, UdpPort: 8006, DeletedAt: deleted),
            ],
        };
        store.Save(s);

        var loaded = new SettingsStore(path).LoadOrCreate();
        Assert.Equal(s.Programs, loaded.Programs);
        Assert.Equal(s.Regions, loaded.Regions);
    }

    [Fact]
    public void Settings_without_custom_types_or_needs_content_load()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        store.Save(store.LoadOrCreate() with
        {
            Programs = [new ProgramSettings("Home", 8004, 8011)],
            Regions = [new RegionSettings("Home", "Home", Guid.NewGuid(), UdpPort: 8004)],
        });
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("customTypes");
        foreach (var region in node["regions"]!.AsArray())
            region!.AsObject().Remove("needsContent");
        File.WriteAllText(path, node.ToJsonString());

        var loaded = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(5, loaded.SchemaVersion);
        Assert.Empty(loaded.CustomTypes);
        Assert.False(loaded.Regions[0].NeedsContent);
    }

    [Fact]
    public void Custom_types_and_needs_content_round_trip()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        var s = store.LoadOrCreate() with
        {
            CustomTypes = [new("c-0a1b2c3d", "My Castle", "castle.oar")],
            Programs = [new ProgramSettings("Home", 8004, 8011)],
            Regions = [new RegionSettings("Home", "Home", Guid.NewGuid(), UdpPort: 8004, NeedsContent: true)],
        };
        store.Save(s);

        var loaded = new SettingsStore(path).LoadOrCreate();
        Assert.Equal(s.CustomTypes, loaded.CustomTypes);
        Assert.True(loaded.Regions[0].NeedsContent);
    }

    /// <summary>A schema 3 settings file: a fresh file rewritten with schemaVersion 3, the given hostname and setup state.</summary>
    private string WriteV3(string? hostname, bool finished, bool worldNamed = false)
    {
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["schemaVersion"] = 3;
        node["hostname"] = hostname;
        node["setup"] = new System.Text.Json.Nodes.JsonObject { ["worldNamed"] = worldNamed, ["finished"] = finished };
        node.Remove("plan");
        node.Remove("hypergrid");
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    [Fact]
    public void Schema_3_finished_with_public_hostname_becomes_simple_public_with_hypergrid_on()
    {
        var s = new SettingsStore(WriteV3("203.0.113.7", finished: true)).LoadOrCreate();

        Assert.Equal(OwnerPlan.SimplePublic, s.Plan);
        Assert.True(s.Hypergrid);
        Assert.Equal(5, s.SchemaVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("192.168.1.20")]
    public void Schema_3_finished_with_lan_or_no_hostname_becomes_local_with_hypergrid_off(string? hostname)
    {
        var s = new SettingsStore(WriteV3(hostname, finished: true)).LoadOrCreate();

        Assert.Equal(OwnerPlan.Local, s.Plan);
        Assert.False(s.Hypergrid);
    }

    [Fact]
    public void Schema4_file_loads_with_default_security()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["schemaVersion"] = 4;
        node.Remove("security");
        File.WriteAllText(path, node.ToJsonString());

        var s = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(100, s.Security.SupervisorLevel);
        Assert.Equal(200, s.Security.AdminLevel);
        Assert.False(s.Security.RequireTwoFactorForStaff);
        Assert.Null(s.Security.AdminNetworks);
        Assert.Equal(5, s.SchemaVersion);
    }

    /// <summary>A saved settings file whose "security" object is <paramref name="security"/> (JSON), as a hand edit would leave it.</summary>
    private string WithSecurity(string security)
    {
        var path = Path.Combine(NewDir(), "settings.json");
        new SettingsStore(path).LoadOrCreate();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["security"] = System.Text.Json.Nodes.JsonNode.Parse(security);
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    [Fact]
    public void Null_security_reads_as_the_defaults()
    {
        var path = WithSecurity("null");

        var s = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(100, s.Security.SupervisorLevel);
        Assert.Equal(200, s.Security.AdminLevel);
        Assert.False(s.Security.RequireTwoFactorForStaff);
        Assert.Null(s.Security.AdminNetworks);
        Assert.Equal(100, new SettingsStore(path).TryLoad()!.Security.SupervisorLevel);
    }

    [Theory]
    [InlineData("""{ "supervisorLevel": 0 }""", "security.supervisorLevel", "Use a supervisor level of 1 or more.")]
    [InlineData("""{ "supervisorLevel": -5, "adminLevel": 200 }""", "security.supervisorLevel", "Use a supervisor level of 1 or more.")]
    [InlineData("""{ "adminLevel": 0 }""", "security.adminLevel", "The admin level must be higher than the supervisor level.")]
    [InlineData("""{ "supervisorLevel": 150, "adminLevel": 150 }""", "security.adminLevel", "The admin level must be higher than the supervisor level.")]
    [InlineData("""{ "adminLevel": 300 }""", "security.adminLevel", "Use an admin level of 255 or less.")]
    public void Security_levels_that_are_not_allowed_refuse_to_load_and_leave_the_file_alone(string security, string key, string reason)
    {
        var path = WithSecurity(security);
        var before = File.ReadAllBytes(path);

        var ex = Assert.Throws<SettingsCorruptException>(() => new SettingsStore(path).LoadOrCreate());
        Assert.Throws<SettingsCorruptException>(() => new SettingsStore(path).TryLoad());

        Assert.Contains(path, ex.Message);
        Assert.Contains($"{key}: {reason}", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Allowed_security_levels_load()
    {
        var s = new SettingsStore(WithSecurity("""{ "supervisorLevel": 1, "adminLevel": 255 }""")).LoadOrCreate();

        Assert.Equal(1, s.Security.SupervisorLevel);
        Assert.Equal(255, s.Security.AdminLevel);
    }

    [Fact]
    public void Schema_3_unfinished_keeps_plan_unset()
    {
        var s = new SettingsStore(WriteV3("203.0.113.7", finished: false, worldNamed: true)).LoadOrCreate();

        Assert.Null(s.Plan);
        Assert.False(s.Hypergrid);
        Assert.True(s.Setup.WorldNamed);
    }

    [Fact]
    public void New_settings_have_no_plan_and_hypergrid_off()
    {
        var s = new SettingsStore(Path.Combine(NewDir(), "settings.json")).LoadOrCreate();

        Assert.Null(s.Plan);
        Assert.False(s.Hypergrid);
    }

    [Fact]
    public void Plan_round_trips_as_its_name()
    {
        var path = Path.Combine(NewDir(), "settings.json");
        var store = new SettingsStore(path);
        store.Save(store.LoadOrCreate() with { Plan = OwnerPlan.GridAsset });

        Assert.Contains("\"plan\": \"GridAsset\"", File.ReadAllText(path));
        Assert.Equal(OwnerPlan.GridAsset, new SettingsStore(path).LoadOrCreate().Plan);
    }
}
