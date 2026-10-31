using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml;
using DreamGrid.Launcher.Core.Api;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LegacyCommandApiTests : IDisposable
{
    private const string MachineId = "machine-id-0123456789abcdef";
    private static readonly Guid RegionUuid = Guid.Parse("6f1f0000-0000-0000-0000-00000000aaaa");

    private readonly FakeGridController _grid = new();
    private readonly FakeRobustAccounts _robust = new FakeRobustAccounts()
        .Add("Diva", "Admin", 100, "divapw")
        .Add("God", "Owner", 200, "godpw");
    private LauncherSettings _settings = new()
    {
        Db = new DatabaseSecrets("r", "r", "o"),
        Console = new ConsoleSecrets("u", "p"),
        MachineId = MachineId,
        Programs = TestSettings.ProgramFor(new RegionSettings("Welcome", "Welcome", RegionUuid)), Regions = TestSettings.RegionsFor(new RegionSettings("Welcome", "Welcome", RegionUuid)),
    };

    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _logs = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LoginThrottle _throttle;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly AuditLog _audit;
    private readonly FakeTwoFactor _twoFactor = new();

    public LegacyCommandApiTests()
    {
        _throttle = new LoginThrottle(_time, null);
        _audit = new AuditLog(Path.Combine(_dir, "audit.log"), _time);
        _grid.SettingsSource = () => _settings;
    }

    public void Dispose()
    {
        _audit.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>The audit log's entries so far (the log keeps the file open for writing).</summary>
    private List<JsonElement> AuditEntries()
    {
        var path = Path.Combine(_dir, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public async Task A_command_that_ran_is_audited_with_the_avatar_and_never_the_password()
    {
        var caller = System.Net.IPAddress.Parse("10.2.3.4");

        await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), caller, Ct);

        var entry = Assert.Single(AuditEntries());
        Assert.Equal("command", entry.GetProperty("action").GetString());
        Assert.Equal("God Owner", entry.GetProperty("actor").GetString());
        Assert.Equal("admin", entry.GetProperty("role").GetString());
        Assert.Equal("StopRobust", entry.GetProperty("target").GetString());
        Assert.Equal("ACK", entry.GetProperty("result").GetString());
        Assert.Equal("10.2.3.4", entry.GetProperty("ip").GetString());
        Assert.DoesNotContain("godpw", ReadAudit());
    }

    private string ReadAudit()
    {
        using var s = new FileStream(Path.Combine(_dir, "audit.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public async Task A_command_run_with_the_MachineId_is_audited_by_the_word_never_the_value()
    {
        await Api().HandleAsync(Req("stopmysql", password: MachineId), null, Ct);

        var entry = Assert.Single(AuditEntries());
        Assert.Equal("MachineId", entry.GetProperty("actor").GetString());
        Assert.Equal("machine", entry.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("actorId").ValueKind);
        Assert.Equal("StopMysql", entry.GetProperty("target").GetString());
        Assert.DoesNotContain(MachineId, ReadAudit());
    }

    [Fact]
    public async Task Refused_and_open_commands_are_not_audited()
    {
        await Api().HandleAsync(Req("StopRobust", "Diva Admin", "divapw"), null, Ct); // level 100: refused
        await Api().HandleAsync(Req("StopRobust", "God Owner", "wrong"), null, Ct);
        await Api().HandleAsync(Req("RegionStatus"), null, Ct); // needs no level
        await Api().HandleAsync(Req("regionlist"), null, Ct);

        Assert.Empty(AuditEntries());
    }

    private LegacyCommandApi Api(params IFeatureModule[] modules) => Api(_robust, modules);

    private LegacyCommandApi Api(IRobustClient robust, params IFeatureModule[] modules) =>
        new(_grid, robust, () => _settings, modules, _throttle, _audit, _twoFactor,
            LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(_logs))).CreateLogger<LegacyCommandApi>());

    private static LegacyRequest Req(string command, string? avatar = null, string? password = null,
        string? regionName = null, Guid? regionUuid = null) =>
        new(command, avatar, password, regionName, regionUuid, new Dictionary<string, string>());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegionStatus_needs_no_password()
    {
        var reply = await Api().HandleAsync(Req("RegionStatus"), null, Ct);

        Assert.Equal("Running", reply);
        Assert.Equal(0, _robust.Lookups);
    }

    [Fact]
    public async Task RegionStatus_reports_the_region_state()
    {
        _grid.RegionState = ProcessState.Starting;
        Assert.Equal("Starting", await Api().HandleAsync(Req("RegionStatus", regionName: "Welcome"), null, Ct));
    }

    [Fact]
    public async Task RegionStatus_for_another_region_is_NAK()
    {
        var reply = await Api().HandleAsync(Req("RegionStatus", regionName: "Elsewhere"), null, Ct);
        Assert.StartsWith("NAK", reply);
    }

    [Fact]
    public async Task RegionStatus_without_a_region_is_NAK()
    {
        _settings = _settings with { Programs = [], Regions = [] };
        _grid.HasRegion = false;
        Assert.StartsWith("NAK", await Api().HandleAsync(Req("RegionStatus"), null, Ct));
    }

    [Fact]
    public async Task StartRegion_without_credentials_is_NAK_permission()
    {
        var reply = await Api().HandleAsync(Req("StartRegion", regionName: "Welcome"), null, Ct);

        Assert.StartsWith("NAK You do not have permission to StartRegion", reply);
        Assert.Empty(_grid.Snapshot());
    }

    [Fact]
    public async Task MachineId_grants_level_1000()
    {
        var reply = await Api().HandleAsync(Req("StopMysql", password: MachineId), null, Ct);

        Assert.Equal("ACK", reply);
        Assert.Equal(["Stop database"], _grid.Snapshot());
        Assert.Equal(0, _robust.Lookups);
    }

    [Fact]
    public async Task A_wrong_MachineId_is_NAK_permission()
    {
        var reply = await Api().HandleAsync(Req("StopMysql", password: MachineId + "x"), null, Ct);

        Assert.StartsWith("NAK You do not have permission to StopMysql", reply);
        Assert.Empty(_grid.Snapshot());
    }

    [Fact]
    public async Task Avatar_level_100_cannot_StopRobust_but_level_200_can()
    {
        var diva = await Api().HandleAsync(Req("StopRobust", "Diva Admin", "divapw"), null, Ct);
        Assert.StartsWith("NAK You do not have permission to StopRobust", diva);
        Assert.Empty(_grid.Snapshot());

        var god = await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct);
        Assert.Equal("ACK", god);
        Assert.Equal(["Stop robust"], _grid.Snapshot());
    }

    [Fact]
    public async Task A_connected_servers_level_0_owner_is_an_administrator_and_a_level_250_stranger_is_not()
    {
        var ownerId = Guid.NewGuid();
        _robust.Add("Estate", "Owner", 0, "estatepw", ownerId).Add("Far", "Admin", 250, "farpw");
        _settings = _settings with
        {
            Plan = OwnerPlan.Connected,
            Connected = new ConnectedGrid("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", null),
            Setup = new SetupState(OwnerCreated: true, Finished: true, Owner: new OwnerInfo("Estate", "Owner", ownerId)),
        };

        Assert.StartsWith("NAK You do not have permission to StartRegion",
            await Api().HandleAsync(Req("StartRegion", "Far Admin", "farpw", regionName: "Welcome"), null, Ct));
        Assert.StartsWith("NAK You do not have permission to StopRobust", await Api().HandleAsync(Req("StopRobust", "Far Admin", "farpw"), null, Ct));
        Assert.Empty(_grid.Snapshot());

        Assert.Equal("ACK", await Api().HandleAsync(Req("StartRegion", "Estate Owner", "estatepw", regionName: "Welcome"), null, Ct));
    }

    [Fact]
    public async Task A_region_servers_level_0_estate_owner_is_an_administrator_and_other_level_0_avatars_are_not()
    {
        var ownerId = Guid.NewGuid();
        _robust.Add("Estate", "Owner", 0, "estatepw", ownerId).Add("Plain", "Person", 0, "plainpw");
        _settings = _settings with
        {
            Plan = OwnerPlan.RegionServer,
            Setup = new SetupState(OwnerCreated: true, Finished: true, Owner: new OwnerInfo("Estate", "Owner", ownerId)),
        };

        Assert.Equal("ACK", await Api().HandleAsync(Req("StopRobust", "Estate Owner", "estatepw"), null, Ct));
        Assert.StartsWith("NAK You do not have permission to StopRobust", await Api().HandleAsync(Req("StopRobust", "Plain Person", "plainpw"), null, Ct));

        // The owner named in setup is the owner on any plan whatever their level, and a level-0 avatar is nobody.
        _settings = _settings with { Plan = OwnerPlan.Local };
        Assert.Equal("ACK", await Api().HandleAsync(Req("StopRobust", "Estate Owner", "estatepw"), null, Ct));
        Assert.StartsWith("NAK You do not have permission to StopRobust", await Api().HandleAsync(Req("StopRobust", "Plain Person", "plainpw"), null, Ct));
    }

    [Theory]
    [InlineData("God Owner", "wrong")]
    [InlineData("God", "godpw")]
    [InlineData("Nobody Here", "godpw")]
    [InlineData(null, "godpw")]
    public async Task A_bad_avatar_or_password_is_NAK_permission(string? avatar, string password)
    {
        var reply = await Api().HandleAsync(Req("StopRobust", avatar, password), null, Ct);

        Assert.StartsWith("NAK You do not have permission to StopRobust", reply);
        Assert.Empty(_grid.Snapshot());
    }

    [Theory]
    [InlineData("StartRegion", "Start")]
    [InlineData("StopRegion", "Stop")]
    [InlineData("RestartRegion", "Restart")]
    public async Task Region_commands_use_the_region_power_menu(string command, string action)
    {
        var reply = await Api().HandleAsync(Req(command, password: MachineId, regionName: "Welcome"), null, Ct);

        Assert.Equal("ACK", reply);
        Assert.Equal([$"RegionPower {action} {RegionUuid}"], _grid.Snapshot());
    }

    [Theory]
    [InlineData("StartRobust", "Start robust")]
    [InlineData("StopRobust", "Stop robust")]
    [InlineData("StartMysql", "Start database")]
    [InlineData("StopMysql", "Stop database")]
    public async Task Process_commands_drive_the_matching_component(string command, string call)
    {
        var reply = await Api().HandleAsync(Req(command, password: MachineId, regionName: "Welcome"), null, Ct);

        Assert.Equal("ACK", reply);
        Assert.Equal([call], _grid.Snapshot());
    }

    [Fact]
    public async Task Region_commands_accept_the_region_uuid()
    {
        Assert.Equal("ACK", await Api().HandleAsync(Req("StopRegion", password: MachineId, regionUuid: RegionUuid), null, Ct));
    }

    [Fact]
    public async Task Region_commands_for_another_region_are_NAK_and_do_nothing()
    {
        var byName = await Api().HandleAsync(Req("StopRegion", password: MachineId, regionName: "Elsewhere"), null, Ct);
        var byUuid = await Api().HandleAsync(Req("StopRegion", password: MachineId, regionUuid: Guid.NewGuid()), null, Ct);

        Assert.StartsWith("NAK", byName);
        Assert.StartsWith("NAK", byUuid);
        Assert.Empty(_grid.Snapshot());
    }

    [Fact]
    public async Task A_failing_component_operation_is_NAK_with_the_reason()
    {
        _grid.ComponentError = new InvalidOperationException("Robust is not running.");

        var reply = await Api().HandleAsync(Req("StartRegion", password: MachineId), null, Ct);

        Assert.Equal("NAK Robust is not running.", reply);
    }

    [Fact]
    public async Task Unknown_command_is_NAK_unknown()
    {
        Assert.Equal("NAK Unknown command Freeze", await Api().HandleAsync(Req("Freeze", password: MachineId), null, Ct));
    }

    [Fact]
    public async Task Command_names_are_case_insensitive()
    {
        Assert.Equal("Running", await Api().HandleAsync(Req("regionstatus"), null, Ct));
        Assert.Equal("ACK", await Api().HandleAsync(Req("stopmysql", password: MachineId), null, Ct));
    }

    [Fact]
    public async Task Regionlist_returns_json_without_a_password()
    {
        var reply = await Api().HandleAsync(Req("regionlist"), null, Ct);

        using var doc = JsonDocument.Parse(reply);
        var region = Assert.Single(doc.RootElement.GetProperty("regions").EnumerateArray());
        Assert.Equal("Welcome", region.GetProperty("name").GetString());
        Assert.Equal(RegionUuid.ToString(), region.GetProperty("uuid").GetString());
        Assert.Equal("Running", region.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Regionlist_is_empty_before_a_region_exists()
    {
        _settings = _settings with { Programs = [], Regions = [] };
        _grid.HasRegion = false;

        using var doc = JsonDocument.Parse(await Api().HandleAsync(Req("regionlist"), null, Ct));
        Assert.Empty(doc.RootElement.GetProperty("regions").EnumerateArray());
    }

    [Fact]
    public async Task Module_commands_are_appended_with_their_levels()
    {
        var api = Api(new PingModule());

        Assert.Equal("pong", await api.HandleAsync(Req("Ping"), null, Ct));
        Assert.StartsWith("NAK You do not have permission to Secret", await api.HandleAsync(Req("Secret"), null, Ct));
        Assert.Equal("ok", await api.HandleAsync(Req("Secret", password: MachineId), null, Ct));
    }

    [Fact]
    public async Task A_changed_MachineId_is_seen_without_a_new_api()
    {
        var api = Api();
        _settings = _settings with { MachineId = "rotated-machine-id-000000000" };

        Assert.StartsWith("NAK You do not have permission", await api.HandleAsync(Req("StopMysql", password: MachineId), null, Ct));
        Assert.Equal("ACK", await api.HandleAsync(Req("StopMysql", password: "rotated-machine-id-000000000"), null, Ct));
    }

    [Fact]
    public async Task Five_bad_passwords_lock_the_avatar_and_a_locked_avatar_never_reaches_Robust()
    {
        var api = Api();
        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++)
            Assert.StartsWith("NAK You do not have permission to StopRobust", await api.HandleAsync(Req("StopRobust", "God Owner", "wrong"), null, Ct));
        var lookups = _robust.Lookups;

        // The right password, with other spacing and case, is refused like a bad one while the name is locked.
        var reply = await api.HandleAsync(Req("StopRobust", "god   owner", "godpw"), null, Ct);

        Assert.StartsWith("NAK You do not have permission to StopRobust", reply);
        Assert.Equal(lookups, _robust.Lookups);
        Assert.Empty(_grid.Snapshot());
        Assert.True(_throttle.IsLocked(SignInDoor.Staff, "God Owner", null));

        _time.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
        Assert.Equal("ACK", await api.HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct));
    }

    [Fact]
    public async Task A_name_locked_on_the_website_is_refused_here_too()
    {
        // The sign-in page and this API share one throttle keyed on the normalised name.
        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++) _throttle.RecordFailure(SignInDoor.Staff, AvatarName.Parse("God  Owner").Key, null);

        Assert.StartsWith("NAK You do not have permission", await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct));
        Assert.Equal(0, _robust.Lookups);
    }

    [Fact]
    public async Task A_good_password_clears_earlier_failures()
    {
        var api = Api();
        for (var i = 0; i < LoginThrottle.NameMaxFailures - 1; i++) await api.HandleAsync(Req("StopRobust", "God Owner", "wrong"), null, Ct);
        Assert.Equal("ACK", await api.HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct));

        await api.HandleAsync(Req("StopRobust", "God Owner", "wrong"), null, Ct);

        Assert.False(_throttle.IsLocked(SignInDoor.Staff, "God Owner", null));
    }

    [Fact]
    public async Task Command_api_counts_failures_by_caller_ip()
    {
        var api = Api();
        var guesser = System.Net.IPAddress.Parse("10.0.0.5");
        for (var i = 0; i < LoginThrottle.IpMaxFailures; i++)
            Assert.StartsWith("NAK You do not have permission to StopRobust", await api.HandleAsync(Req("StopRobust", "Guess" + i + " Person", "wrong"), guesser, Ct));
        var lookups = _robust.Lookups;

        // The owner's right password from the same address is refused without asking Robust.
        Assert.StartsWith("NAK You do not have permission to StopRobust", await api.HandleAsync(Req("StopRobust", "God Owner", "godpw"), guesser, Ct));
        Assert.Equal(lookups, _robust.Lookups);

        // The same IPv4 address in its mapped form is the same caller; another address is not locked.
        Assert.StartsWith("NAK You do not have permission to StopRobust",
            await api.HandleAsync(Req("StopRobust", "God Owner", "godpw"), System.Net.IPAddress.Parse("::ffff:10.0.0.5"), Ct));
        Assert.Equal("ACK", await api.HandleAsync(Req("StopRobust", "God Owner", "godpw"), System.Net.IPAddress.Parse("10.0.0.6"), Ct));
        // The MachineId is never throttled, whatever the address.
        Assert.Equal("ACK", await api.HandleAsync(Req("StopMysql", password: MachineId), guesser, Ct));
    }

    [Fact]
    public async Task The_MachineId_is_not_throttled()
    {
        var api = Api();
        for (var i = 0; i < LoginThrottle.NameMaxFailures + 2; i++) await api.HandleAsync(Req("StopMysql", password: MachineId + "x"), null, Ct);

        Assert.Equal("ACK", await api.HandleAsync(Req("StopMysql", password: MachineId), null, Ct));
    }

    [Fact]
    public async Task A_refused_locked_name_is_logged_by_first_name_only()
    {
        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++) _throttle.RecordFailure(SignInDoor.Staff, "God Owner", null);

        await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct);

        Assert.Contains(_logs, l => l.Message.Contains("God") && l.Message.Contains("refused"));
        Assert.DoesNotContain(_logs, l => l.Message.Contains("Owner") || l.Message.Contains("godpw"));
    }

    public static TheoryData<Exception> RobustFailures() => new()
    {
        new TimeoutException("robust slow"),
        new TaskCanceledException("HttpClient timeout"), // HttpClient.Timeout, not the caller's token
        new XmlException("bad xml"),
        new HttpRequestException("refused"),
    };

    [Theory]
    [MemberData(nameof(RobustFailures))]
    public async Task A_failing_Robust_lookup_is_level_0_and_logged_without_secrets(Exception failure)
    {
        var reply = await Api(new ThrowingRobust(failure)).HandleAsync(Req("StopRobust", "God Owner", "sekrit-avatar-pw"), null, Ct);

        Assert.StartsWith("NAK You do not have permission to StopRobust", reply);
        Assert.Empty(_grid.Snapshot());
        Assert.Contains(_logs, l => l.Level == LogLevel.Warning);
        Assert.DoesNotContain(_logs, l => l.Message.Contains("sekrit-avatar-pw") || l.Message.Contains(MachineId));
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_turned_into_a_NAK()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var robust = new ThrowingRobust(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Api(robust).HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, cts.Token));
    }

    private sealed class ThrowingRobust(Exception failure) : IRobustClient
    {
        public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<RobustAccount?> FindAccountAsync(string first, string last, CancellationToken ct) => GetAccountAsync(first, last, ct);

        public Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct) => Task.FromException<RobustAccount?>(failure);
        public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MapRegion>>([]);
        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(MapArea area, CancellationToken ct) => GetMapRegionsAsync(ct);
    }

    private sealed class PingModule : IFeatureModule
    {
        public string Id => "ping";
        public string Title => "Ping";
        public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>();
        public IEnumerable<IniOverride> Contribute(LauncherSettings s) => [];

        public IEnumerable<LegacyCommand> Commands =>
        [
            new("Ping", 0, (_, _) => Task.FromResult("pong")),
            new("Secret", 200, (_, _) => Task.FromResult("ok")),
        ];
    }

    // ---- two-factor and the required-for-staff switch (plan 7a) ----

    private sealed class FakeTwoFactor : ITwoFactorStatus
    {
        private readonly Dictionary<Guid, TwoFactorState> _states = new();
        public void Set(Guid principal, TwoFactorState state) => _states[principal] = state;
        public TwoFactorState StateOf(Guid principal) => _states.GetValueOrDefault(principal, TwoFactorState.Off);
    }

    /// <summary>A command a supervisor (level 100) may run.</summary>
    private sealed class PeekModule : IFeatureModule
    {
        public string Id => "peek";
        public string Title => "Peek";
        public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>();
        public IEnumerable<IniOverride> Contribute(LauncherSettings s) => [];
        public IEnumerable<LegacyCommand> Commands => [new("Peek", 100, (_, _) => Task.FromResult("seen"))];
    }

    private static readonly Guid GodId = Guid.Parse("0a000000-0000-0000-0000-000000000001");
    private static readonly Guid DivaId = Guid.Parse("0a000000-0000-0000-0000-000000000002");
    private static readonly Guid BossId = Guid.Parse("0a000000-0000-0000-0000-000000000003");

    private void KnownIds()
    {
        _robust.Add("God", "Owner", 200, "godpw", GodId).Add("Diva", "Admin", 100, "divapw", DivaId)
            .Add("Boss", "Person", 0, "bosspw", BossId);
        _settings = _settings with
        {
            Setup = new SetupState(OwnerCreated: true, Finished: true, Owner: new OwnerInfo("Boss", "Person", BossId)),
        };
    }

    private void RequireTwoFactor() =>
        _settings = _settings with { Security = _settings.Security with { RequireTwoFactorForStaff = true } };

    [Theory]
    [InlineData(TwoFactorState.On)]
    [InlineData(TwoFactorState.Unknown)]
    public async Task Avatar_with_two_factor_refused_by_password(TwoFactorState state)
    {
        KnownIds();
        _twoFactor.Set(GodId, state);
        var api = Api();

        // Refused as level 0, but the password was right, so it is never counted as a failure.
        for (var i = 0; i < LoginThrottle.NameMaxFailures + 1; i++)
            Assert.StartsWith("NAK You do not have permission to StopRobust", await api.HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct));

        Assert.Empty(_grid.Snapshot());
        Assert.False(_throttle.IsLocked(SignInDoor.Staff, "God Owner", null));
        Assert.Empty(AuditEntries());
        // The MachineId still works.
        Assert.Equal("ACK", await api.HandleAsync(Req("StopRobust", password: MachineId), null, Ct));
    }

    [Theory]
    [InlineData(TwoFactorState.On, false)]
    [InlineData(TwoFactorState.Off, true)]
    public async Task A_right_password_that_is_refused_does_not_clear_earlier_failures(TwoFactorState state, bool required)
    {
        // Otherwise a known password sent here would reset the name's count between guesses at the two-factor code.
        KnownIds();
        _twoFactor.Set(DivaId, state);
        if (required) RequireTwoFactor();
        var api = Api();
        for (var i = 0; i < LoginThrottle.NameMaxFailures - 1; i++) _throttle.RecordFailure(SignInDoor.Staff, "Diva Admin", null);

        Assert.StartsWith("NAK You do not have permission to StopRobust", await api.HandleAsync(Req("StopRobust", "Diva Admin", "divapw"), null, Ct));
        _throttle.RecordFailure(SignInDoor.Staff, "Diva Admin", null);

        Assert.True(_throttle.IsLocked(SignInDoor.Staff, "Diva Admin", null));
    }

    [Fact]
    public async Task A_reply_that_is_not_an_account_is_level_0_and_never_a_failure()
    {
        _robust.FailureReply = true;
        var api = Api();

        for (var i = 0; i < LoginThrottle.NameMaxFailures + 1; i++)
            Assert.StartsWith("NAK You do not have permission to StopRobust", await api.HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct));

        Assert.Empty(_grid.Snapshot());
        Assert.False(_throttle.IsLocked(SignInDoor.Staff, "God Owner", null));
        Assert.Contains(_logs, l => l.Level == LogLevel.Warning && l.Message.Contains(nameof(InvalidDataException)));
    }

    [Fact]
    public async Task A_refusal_for_two_factor_is_logged_by_first_name_only_and_never_the_password()
    {
        KnownIds();
        _twoFactor.Set(GodId, TwoFactorState.On);

        await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct);

        var line = Assert.Single(_logs, l => l.Level == LogLevel.Information);
        Assert.Contains("God", line.Message);
        Assert.Contains("two-factor", line.Message);
        Assert.Contains("MachineId", line.Message);
        Assert.DoesNotContain("Owner", line.Message);
        Assert.DoesNotContain("godpw", line.Message);
        Assert.DoesNotContain(MachineId, line.Message);
    }

    [Fact]
    public async Task The_owner_with_two_factor_on_is_refused_by_password_too()
    {
        KnownIds();
        _twoFactor.Set(BossId, TwoFactorState.On);

        Assert.StartsWith("NAK You do not have permission to StopRobust", await Api().HandleAsync(Req("StopRobust", "Boss Person", "bosspw"), null, Ct));
        Assert.Empty(_grid.Snapshot());
    }

    [Fact]
    public async Task Required_switch_refuses_admin_password()
    {
        KnownIds();
        RequireTwoFactor(); // God has no two-factor

        Assert.StartsWith("NAK You do not have permission to StopRobust", await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct));
        Assert.Empty(_grid.Snapshot());
        Assert.False(_throttle.IsLocked(SignInDoor.Staff, "God Owner", null));
        Assert.Contains(_logs, l => l.Level == LogLevel.Information && l.Message.Contains("God") && l.Message.Contains("two-factor"));

        // With the switch off the same password works.
        _settings = _settings with { Security = _settings.Security with { RequireTwoFactorForStaff = false } };
        Assert.Equal("ACK", await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), null, Ct));
    }

    [Fact]
    public async Task Required_switch_refuses_supervisor_password()
    {
        KnownIds();
        var api = Api(new PeekModule());
        Assert.Equal("seen", await api.HandleAsync(Req("Peek", "Diva Admin", "divapw"), null, Ct));

        RequireTwoFactor();

        Assert.StartsWith("NAK You do not have permission to Peek", await api.HandleAsync(Req("Peek", "Diva Admin", "divapw"), null, Ct));
    }

    [Fact]
    public async Task Owner_without_two_factor_still_allowed_when_switch_on()
    {
        KnownIds();
        RequireTwoFactor();

        Assert.Equal("ACK", await Api().HandleAsync(Req("StopRobust", "Boss Person", "bosspw"), null, Ct));
        Assert.Equal(["Stop robust"], _grid.Snapshot());
    }

    [Fact]
    public async Task MachineId_unaffected()
    {
        KnownIds();
        RequireTwoFactor();
        foreach (var id in new[] { GodId, DivaId, BossId }) _twoFactor.Set(id, TwoFactorState.Unknown);

        Assert.Equal("ACK", await Api().HandleAsync(Req("StopMysql", password: MachineId), null, Ct));
        Assert.Equal(0, _robust.Lookups);
    }

    // ---- many regions (plan 4a) ----

    private static readonly Guid BeachUuid = Guid.Parse("6f1f0000-0000-0000-0000-00000000bbbb");

    private void AddBeach() => _settings = _settings with
    {
        Regions = [.. _settings.Regions, new RegionSettings("Welcome", "Beach", BeachUuid, 1001, 1000, UdpPort: 8005)],
    };

    [Fact]
    public async Task Regionlist_lists_all_regions()
    {
        AddBeach();

        var reply = await Api().HandleAsync(Req("regionlist"), null, Ct);

        Assert.Contains("\"Welcome\"", reply);
        Assert.Contains("\"Beach\"", reply);
        Assert.Contains(BeachUuid.ToString(), reply);
    }

    [Fact]
    public async Task Region_status_needs_a_name_when_there_are_several()
    {
        AddBeach();

        Assert.Equal("NAK Name the region.", await Api().HandleAsync(Req("RegionStatus"), null, Ct));
        Assert.Equal("Running", await Api().HandleAsync(Req("RegionStatus", regionName: "beach"), null, Ct));
    }

    [Fact]
    public async Task Stop_region_is_a_stop_not_a_park()
    {
        AddBeach();

        Assert.Equal("ACK", await Api().HandleAsync(Req("StopRegion", password: MachineId, regionName: "Beach"), null, Ct));
        Assert.Equal([$"RegionPower Stop {BeachUuid}"], _grid.Snapshot());
    }
}
