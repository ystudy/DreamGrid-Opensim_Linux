using System.Text.Json.Serialization;

namespace DreamGrid.Launcher.Core.Settings;

public sealed record DatabaseSecrets(string RootPassword, string RobustPassword, string OpensimPassword);

public sealed record ConsoleSecrets(string User, string Password);

/// <summary>The owner's password is never stored.</summary>
public sealed record OwnerInfo(string FirstName, string LastName, Guid PrincipalId);

public sealed record SetupState(
    bool WorldNamed = false,
    bool OwnerCreated = false,
    bool RegionCreated = false,
    bool Finished = false,
    OwnerInfo? Owner = null);

/// <summary>
/// One OpenSim program (instance) hosting one or more regions, in <c>Regions/&lt;Instance&gt;/</c>.
/// <see cref="Physics"/> is read once per program by OpenSim, so every region in it shares it.
/// </summary>
public sealed record ProgramSettings(string Instance, int HttpPort, int ConsolePort, string Physics = "ubODE");

/// <summary>
/// One region. <see cref="Instance"/> names its program; <see cref="Size"/> is squares per side (256 m each);
/// <see cref="X"/> and <see cref="Y"/> are its south-west square on the map. A parked region stays off until unparked;
/// a deleted one (<see cref="DeletedAt"/> set) keeps its database rows and its map spot until erased.
/// </summary>
public sealed record RegionSettings(
    string Instance,
    string Name,
    Guid Uuid,
    int X = 1000,
    int Y = 1000,
    string Content = "starter",
    int Size = 1,
    int UdpPort = 0,
    bool Parked = false,
    DateTimeOffset? DeletedAt = null);

/// <summary>Version 1's single region, read only to migrate a schema 1 file.</summary>
public sealed record LegacyRegionV1(string Instance, string Name, Guid Uuid, int X = 1000, int Y = 1000, string Physics = "ubODE",
    string Content = "starter");

/// <summary><see cref="Target"/> is "robust" or "region".</summary>
public sealed record IniOverride(string Target, string Section, string Key, string Value);

public sealed record LauncherSettings
{
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; init; } = CurrentSchema;
    public string GridName { get; init; } = "My World";
    /// <summary>Null means the detected LAN IP.</summary>
    public string? Hostname { get; init; }

    public int WebPort { get; init; } = 8001;
    public int RobustPublicPort { get; init; } = 8002;
    public int RobustPrivatePort { get; init; } = 8003;
    /// <summary>The first port tried for a new program's HTTP port and a new region's UDP port.</summary>
    public int RegionPort { get; init; } = 8004;
    public int MariaDbPort { get; init; } = 3307;
    public int RobustConsolePort { get; init; } = 8010;
    /// <summary>The first port tried for a new program's REST console.</summary>
    public int RegionConsolePort { get; init; } = 8011;

    public required DatabaseSecrets Db { get; init; }
    public required ConsoleSecrets Console { get; init; }
    public required string MachineId { get; init; }
    public string? SetupToken { get; init; }

    public SetupState Setup { get; init; } = new();
    public List<ProgramSettings> Programs { get; init; } = new();
    public List<RegionSettings> Regions { get; init; } = new();

    /// <summary>Schema 1's "region"; <see cref="SettingsStore"/> migrates it and it is never written again.</summary>
    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyRegionV1? LegacyRegion { get; init; }

    public Dictionary<string, Dictionary<string, string>> Modules { get; init; } = new();
    public List<IniOverride> Overrides { get; init; } = new();
}
