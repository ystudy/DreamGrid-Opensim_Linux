using System.Diagnostics.CodeAnalysis;
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
    OwnerInfo? Owner = null,
    bool NetworkSet = false,
    bool RegionSkipped = false);

/// <summary>How a map tile is drawn; "None" turns map tiles off.</summary>
public enum MapQuality { None, Simple, Good, Better, Best }

/// <summary>The operating system's priority for a program's process.</summary>
public enum CpuPriority { BelowNormal, Normal, AboveNormal, High, RealTime }

/// <summary>
/// Per-region limits. A null number means OpenSim's own default and its key is not written.
/// </summary>
public sealed record RegionOptions(
    int? MaxAgents = null,
    int? MaxPrims = null,
    int? MaxNonPhysicalSize = null,
    int? MaxPhysicalSize = null,
    bool ClampSize = false,
    bool KeepResidentsOut = false,
    bool AnnounceVisitors = false);

/// <summary>
/// Per-program options. <see cref="Scripts"/> null means the grid default; <see cref="Cores"/> null or empty means every core.
/// </summary>
public sealed record ProgramOptions(
    bool? Scripts = null,
    double? MinTimerInterval = null,
    int? FramesPerSecond = null,
    MapQuality Map = MapQuality.Simple,
    bool AllowGods = true,
    bool OwnerIsGod = true,
    bool ManagerIsGod = false,
    IReadOnlyList<int>? Cores = null,
    CpuPriority Priority = CpuPriority.Normal);

/// <summary>
/// One OpenSim program (instance) hosting one or more regions, in <c>Regions/&lt;Instance&gt;/</c>.
/// <see cref="Physics"/> is read once per program by OpenSim, so every region in it shares it. It is null (the grid
/// default), "ubODE", "BulletSim" or "BulletSimThreaded". A missing <see cref="Options"/> reads as <c>new ProgramOptions()</c>.
/// </summary>
public sealed record ProgramSettings(string Instance, int HttpPort, int ConsolePort, string? Physics = null, ProgramOptions? Options = null);

/// <summary>
/// One region. <see cref="Instance"/> names its program; <see cref="Size"/> is squares per side (256 m each);
/// <see cref="X"/> and <see cref="Y"/> are its south-west square on the map. A parked region stays off until unparked;
/// a deleted one (<see cref="DeletedAt"/> set) keeps its database rows and its map spot until erased.
/// <see cref="NeedsContent"/> marks a region whose <see cref="Content"/> has not been loaded yet.
/// A missing <see cref="Options"/> reads as <c>new RegionOptions()</c>.
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
    DateTimeOffset? DeletedAt = null,
    bool NeedsContent = false,
    RegionOptions? Options = null);

/// <summary>A region type made from a file in the OAR library; <see cref="Id"/> is "c-" and 8 lowercase hex characters.</summary>
public sealed record CustomRegionType(string Id, string Name, string File);

/// <summary>Version 1's single region, read only to migrate a schema 1 file.</summary>
public sealed record LegacyRegionV1(string Instance, string Name, Guid Uuid, int X = 1000, int Y = 1000, string Physics = "ubODE",
    string Content = "starter");

/// <summary><see cref="Target"/> is "robust" or "region".</summary>
public sealed record IniOverride(string Target, string Section, string Key, string Value);

/// <summary>
/// The Grid Asset Server a Region Server has joined: its address, the grid service's ports, the grid's name and
/// version, whether hypergrid is on, and the region ports it publishes (null when none). Never holds a password.
/// </summary>
public sealed record JoinedGrid(
    string Host, int PublicPort, int PrivatePort, string GridName, string LoginUri, string Version, bool Hypergrid,
    int? RegionPortsFrom, int? RegionPortsTo);

/// <summary>
/// The grid a Connected Server's regions are on: a preset's id (null for another grid), where this computer reaches its
/// login service, the login address the grid publishes, the address for region servers, and its gatekeeper (null when it
/// publishes none). Never holds a password.
/// </summary>
public sealed record ConnectedGrid(
    string? Preset, string GridName, string Address, string LoginUri, string ServicesUri, string? GatekeeperUri);

/// <summary>
/// Sign-in security. An avatar at <see cref="SupervisorLevel"/> or more is a supervisor, at <see cref="AdminLevel"/> or more an
/// administrator. <see cref="AdminNetworks"/> (CIDR ranges) null or empty means any address may reach the staff pages.
/// </summary>
public sealed record SecuritySettings(
    int SupervisorLevel = 100,
    int AdminLevel = 200,
    bool RequireTwoFactorForStaff = false,
    IReadOnlyList<string>? AdminNetworks = null);

/// <summary>One legal page's text as the operator wrote it (Markdown), how many times it was saved and when.</summary>
public sealed record LegalText(string Markdown, int Version, DateTimeOffset Saved);

/// <summary>
/// The operator's own legal pages. <see cref="Texts"/>: language ("en") to page id ("terms", "privacy", "cookies", "contact") to
/// text; null or empty means none written. DreamGrid ships no wording. <see cref="OperatorResponsible"/> is the operator's tick that they
/// are responsible for the texts; <see cref="ResponsibleSince"/> is when it was ticked.
/// </summary>
public sealed record LegalSettings(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, LegalText>>? Texts = null,
    bool OperatorResponsible = false,
    DateTimeOffset? ResponsibleSince = null);

/// <summary>
/// The owner's wording for the grid's emails: language ("en") to template id to text, only where it differs from the default
/// (<see cref="Email.EmailTemplates"/>); null or empty means every template uses its default.
/// </summary>
public sealed record EmailSettings(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, Email.EmailTemplateText>>? Templates = null);

/// <summary>
/// Protection of the sign-up form against bots. <see cref="Difficulty"/> is the proof-of-work's largest number to try
/// (ALTCHA <c>maxnumber</c>, 1 000 to 1 000 000); <see cref="PerHourPerIp"/> is 1 to 1 000.
/// </summary>
public sealed record BotSettings(
    bool Honeypot = true,
    bool RateLimit = true,
    int PerHourPerIp = 5,
    bool ProofOfWork = true,
    int Difficulty = 50_000,
    bool ImageCaptcha = false,
    bool AdminApproval = false);

public sealed record LauncherSettings
{
    public const int CurrentSchema = 6;

    public int SchemaVersion { get; init; } = CurrentSchema;
    public string GridName { get; init; } = "My World";
    /// <summary>Null means the detected LAN IP.</summary>
    public string? Hostname { get; init; }

    /// <summary>The owner plan chosen at first use; null until one is chosen.</summary>
    public OwnerPlan? Plan { get; init; }
    /// <summary>Whether avatars may travel to and from other grids.</summary>
    public bool Hypergrid { get; init; }

    /// <summary>The Grid Asset Server this Region Server joined; null until joined.</summary>
    public JoinedGrid? Join { get; init; }

    /// <summary>The other grid this Connected Server's regions are on; null until connected.</summary>
    public ConnectedGrid? Connected { get; init; }

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

    /// <summary>The physics engine a program without its own uses: "ubODE", "BulletSim" or "BulletSimThreaded".</summary>
    public string DefaultPhysics { get; init; } = "ubODE";
    /// <summary>Whether a program without its own setting runs scripts.</summary>
    public bool DefaultScripts { get; init; } = true;

    public SetupState Setup { get; init; } = new();
    private readonly SecuritySettings _security = new();

    /// <summary>Sign-in security; a missing or null value (a hand edit) reads as the defaults.</summary>
    [AllowNull]
    public SecuritySettings Security { get => _security; init => _security = value ?? new(); }

    private readonly LegalSettings _legal = new();

    /// <summary>The operator's legal pages; a missing or null value reads as none written.</summary>
    [AllowNull]
    public LegalSettings Legal { get => _legal; init => _legal = value ?? new(); }

    private readonly EmailSettings _email = new();

    /// <summary>The owner's email wording; a missing or null value reads as every default.</summary>
    [AllowNull]
    public EmailSettings Email { get => _email; init => _email = value ?? new(); }

    private readonly BotSettings _bots = new();

    /// <summary>Bot protection for sign-up; a missing or null value reads as the defaults.</summary>
    [AllowNull]
    public BotSettings Bots { get => _bots; init => _bots = value ?? new(); }
    public List<ProgramSettings> Programs { get; init; } = new();
    public List<RegionSettings> Regions { get; init; } = new();
    /// <summary>Region types made from library files, in the order they were added; a missing list reads as empty.</summary>
    public List<CustomRegionType> CustomTypes { get; init; } = new();

    /// <summary>Schema 1's "region"; <see cref="SettingsStore"/> migrates it and it is never written again.</summary>
    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyRegionV1? LegacyRegion { get; init; }

    public Dictionary<string, Dictionary<string, string>> Modules { get; init; } = new();
    public List<IniOverride> Overrides { get; init; } = new();
}
