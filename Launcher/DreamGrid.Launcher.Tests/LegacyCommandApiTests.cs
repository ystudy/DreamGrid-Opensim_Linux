using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml;
using DreamGrid.Launcher.Core.Api;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LegacyCommandApiTests
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
        Region = new RegionSettings("Welcome", "Welcome", RegionUuid),
    };

    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _logs = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LoginThrottle _throttle;

    public LegacyCommandApiTests() => _throttle = new LoginThrottle(_time);

    private LegacyCommandApi Api(params IFeatureModule[] modules) => Api(_robust, modules);

    private LegacyCommandApi Api(IRobustClient robust, params IFeatureModule[] modules) =>
        new(_grid, robust, () => _settings, modules, _throttle,
            LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(_logs))).CreateLogger<LegacyCommandApi>());

    private static LegacyRequest Req(string command, string? avatar = null, string? password = null,
        string? regionName = null, Guid? regionUuid = null) =>
        new(command, avatar, password, regionName, regionUuid, new Dictionary<string, string>());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegionStatus_needs_no_password()
    {
        var reply = await Api().HandleAsync(Req("RegionStatus"), Ct);

        Assert.Equal("Running", reply);
        Assert.Equal(0, _robust.Lookups);
    }

    [Fact]
    public async Task RegionStatus_reports_the_region_state()
    {
        _grid.RegionState = ProcessState.Starting;
        Assert.Equal("Starting", await Api().HandleAsync(Req("RegionStatus", regionName: "Welcome"), Ct));
    }

    [Fact]
    public async Task RegionStatus_for_another_region_is_NAK()
    {
        var reply = await Api().HandleAsync(Req("RegionStatus", regionName: "Elsewhere"), Ct);
        Assert.StartsWith("NAK", reply);
    }

    [Fact]
    public async Task RegionStatus_without_a_region_is_NAK()
    {
        _settings = _settings with { Region = null };
        _grid.HasRegion = false;
        Assert.StartsWith("NAK", await Api().HandleAsync(Req("RegionStatus"), Ct));
    }

    [Fact]
    public async Task StartRegion_without_credentials_is_NAK_permission()
    {
        var reply = await Api().HandleAsync(Req("StartRegion", regionName: "Welcome"), Ct);

        Assert.StartsWith("NAK You do not have permission to StartRegion", reply);
        Assert.Empty(_grid.Snapshot());
    }

    [Fact]
    public async Task MachineId_grants_level_1000()
    {
        var reply = await Api().HandleAsync(Req("StopMysql", password: MachineId), Ct);

        Assert.Equal("ACK", reply);
        Assert.Equal(["Stop database"], _grid.Snapshot());
        Assert.Equal(0, _robust.Lookups);
    }

    [Fact]
    public async Task A_wrong_MachineId_is_NAK_permission()
    {
        var reply = await Api().HandleAsync(Req("StopMysql", password: MachineId + "x"), Ct);

        Assert.StartsWith("NAK You do not have permission to StopMysql", reply);
        Assert.Empty(_grid.Snapshot());
    }

    [Fact]
    public async Task Avatar_level_100_cannot_StopRobust_but_level_200_can()
    {
        var diva = await Api().HandleAsync(Req("StopRobust", "Diva Admin", "divapw"), Ct);
        Assert.StartsWith("NAK You do not have permission to StopRobust", diva);
        Assert.Empty(_grid.Snapshot());

        var god = await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), Ct);
        Assert.Equal("ACK", god);
        Assert.Equal(["Stop robust"], _grid.Snapshot());
    }

    [Theory]
    [InlineData("God Owner", "wrong")]
    [InlineData("God", "godpw")]
    [InlineData("Nobody Here", "godpw")]
    [InlineData(null, "godpw")]
    public async Task A_bad_avatar_or_password_is_NAK_permission(string? avatar, string password)
    {
        var reply = await Api().HandleAsync(Req("StopRobust", avatar, password), Ct);

        Assert.StartsWith("NAK You do not have permission to StopRobust", reply);
        Assert.Empty(_grid.Snapshot());
    }

    [Theory]
    [InlineData("StartRegion", "Start region")]
    [InlineData("StopRegion", "Stop region")]
    [InlineData("RestartRegion", "Restart region")]
    [InlineData("StartRobust", "Start robust")]
    [InlineData("StopRobust", "Stop robust")]
    [InlineData("StartMysql", "Start database")]
    [InlineData("StopMysql", "Stop database")]
    public async Task Process_commands_drive_the_matching_component(string command, string call)
    {
        var reply = await Api().HandleAsync(Req(command, password: MachineId, regionName: "Welcome"), Ct);

        Assert.Equal("ACK", reply);
        Assert.Equal([call], _grid.Snapshot());
    }

    [Fact]
    public async Task Region_commands_accept_the_region_uuid()
    {
        Assert.Equal("ACK", await Api().HandleAsync(Req("StopRegion", password: MachineId, regionUuid: RegionUuid), Ct));
    }

    [Fact]
    public async Task Region_commands_for_another_region_are_NAK_and_do_nothing()
    {
        var byName = await Api().HandleAsync(Req("StopRegion", password: MachineId, regionName: "Elsewhere"), Ct);
        var byUuid = await Api().HandleAsync(Req("StopRegion", password: MachineId, regionUuid: Guid.NewGuid()), Ct);

        Assert.StartsWith("NAK", byName);
        Assert.StartsWith("NAK", byUuid);
        Assert.Empty(_grid.Snapshot());
    }

    [Fact]
    public async Task A_failing_component_operation_is_NAK_with_the_reason()
    {
        _grid.ComponentError = new InvalidOperationException("Robust is not running.");

        var reply = await Api().HandleAsync(Req("StartRegion", password: MachineId), Ct);

        Assert.Equal("NAK Robust is not running.", reply);
    }

    [Fact]
    public async Task Unknown_command_is_NAK_unknown()
    {
        Assert.Equal("NAK Unknown command Freeze", await Api().HandleAsync(Req("Freeze", password: MachineId), Ct));
    }

    [Fact]
    public async Task Command_names_are_case_insensitive()
    {
        Assert.Equal("Running", await Api().HandleAsync(Req("regionstatus"), Ct));
        Assert.Equal("ACK", await Api().HandleAsync(Req("stopmysql", password: MachineId), Ct));
    }

    [Fact]
    public async Task Regionlist_returns_json_without_a_password()
    {
        var reply = await Api().HandleAsync(Req("regionlist"), Ct);

        using var doc = JsonDocument.Parse(reply);
        var region = Assert.Single(doc.RootElement.GetProperty("regions").EnumerateArray());
        Assert.Equal("Welcome", region.GetProperty("name").GetString());
        Assert.Equal(RegionUuid.ToString(), region.GetProperty("uuid").GetString());
        Assert.Equal("Running", region.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Regionlist_is_empty_before_a_region_exists()
    {
        _settings = _settings with { Region = null };
        _grid.HasRegion = false;

        using var doc = JsonDocument.Parse(await Api().HandleAsync(Req("regionlist"), Ct));
        Assert.Empty(doc.RootElement.GetProperty("regions").EnumerateArray());
    }

    [Fact]
    public async Task Module_commands_are_appended_with_their_levels()
    {
        var api = Api(new PingModule());

        Assert.Equal("pong", await api.HandleAsync(Req("Ping"), Ct));
        Assert.StartsWith("NAK You do not have permission to Secret", await api.HandleAsync(Req("Secret"), Ct));
        Assert.Equal("ok", await api.HandleAsync(Req("Secret", password: MachineId), Ct));
    }

    [Fact]
    public async Task A_changed_MachineId_is_seen_without_a_new_api()
    {
        var api = Api();
        _settings = _settings with { MachineId = "rotated-machine-id-000000000" };

        Assert.StartsWith("NAK You do not have permission", await api.HandleAsync(Req("StopMysql", password: MachineId), Ct));
        Assert.Equal("ACK", await api.HandleAsync(Req("StopMysql", password: "rotated-machine-id-000000000"), Ct));
    }

    [Fact]
    public async Task Five_bad_passwords_lock_the_avatar_and_a_locked_avatar_never_reaches_Robust()
    {
        var api = Api();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            Assert.StartsWith("NAK You do not have permission to StopRobust", await api.HandleAsync(Req("StopRobust", "God Owner", "wrong"), Ct));
        var lookups = _robust.Lookups;

        // The right password, with other spacing and case, is refused like a bad one while the name is locked.
        var reply = await api.HandleAsync(Req("StopRobust", "god   owner", "godpw"), Ct);

        Assert.StartsWith("NAK You do not have permission to StopRobust", reply);
        Assert.Equal(lookups, _robust.Lookups);
        Assert.Empty(_grid.Snapshot());
        Assert.True(_throttle.IsLocked("God Owner"));

        _time.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
        Assert.Equal("ACK", await api.HandleAsync(Req("StopRobust", "God Owner", "godpw"), Ct));
    }

    [Fact]
    public async Task A_name_locked_on_the_website_is_refused_here_too()
    {
        // The sign-in page and this API share one throttle keyed on the normalised name.
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) _throttle.RecordFailure(AvatarName.Parse("God  Owner").Key);

        Assert.StartsWith("NAK You do not have permission", await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), Ct));
        Assert.Equal(0, _robust.Lookups);
    }

    [Fact]
    public async Task A_good_password_clears_earlier_failures()
    {
        var api = Api();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) await api.HandleAsync(Req("StopRobust", "God Owner", "wrong"), Ct);
        Assert.Equal("ACK", await api.HandleAsync(Req("StopRobust", "God Owner", "godpw"), Ct));

        await api.HandleAsync(Req("StopRobust", "God Owner", "wrong"), Ct);

        Assert.False(_throttle.IsLocked("God Owner"));
    }

    [Fact]
    public async Task The_MachineId_is_not_throttled()
    {
        var api = Api();
        for (var i = 0; i < LoginThrottle.MaxFailures + 2; i++) await api.HandleAsync(Req("StopMysql", password: MachineId + "x"), Ct);

        Assert.Equal("ACK", await api.HandleAsync(Req("StopMysql", password: MachineId), Ct));
    }

    [Fact]
    public async Task A_refused_locked_name_is_logged_by_first_name_only()
    {
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) _throttle.RecordFailure("God Owner");

        await Api().HandleAsync(Req("StopRobust", "God Owner", "godpw"), Ct);

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
        var reply = await Api(new ThrowingRobust(failure)).HandleAsync(Req("StopRobust", "God Owner", "sekrit-avatar-pw"), Ct);

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
            Api(robust).HandleAsync(Req("StopRobust", "God Owner", "godpw"), cts.Token));
    }

    private sealed class ThrowingRobust(Exception failure) : IRobustClient
    {
        public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct) => Task.FromException<RobustAccount?>(failure);
        public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) => Task.FromResult(true);
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
}
