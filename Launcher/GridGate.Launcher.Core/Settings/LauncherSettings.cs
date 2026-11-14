using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using GridGate.Launcher.Core.Money;

namespace GridGate.Launcher.Core.Settings;

/// <summary><paramref name="MoneyPassword"/> is empty until money is first turned on (money design §7.1).</summary>
public sealed record DatabaseSecrets(string RootPassword, string RobustPassword, string OpensimPassword, string MoneyPassword = "");

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

/// <summary>
/// Where people land when their home or last region can't take them (2.14). <see cref="Default"/>: the region chosen, null for
/// automatic (the first region). <see cref="Fallbacks"/>: regions tried when the default is down too; Robust takes the nearest
/// running one, so the order is not kept. Both are region UUIDs; one that is deleted or unknown is ignored.
/// </summary>
public sealed record LandingSettings(Guid? Default = null, IReadOnlyList<Guid>? Fallbacks = null);

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

/// <summary>How someone who forgot their password gets back in: not by themselves, by an email link, or by the link and their two-factor code.</summary>
public enum RecoveryRule { Off, EmailLink, EmailLinkAndCode }

/// <summary>The password-recovery rule for each group (see <see cref="Security.Recovery"/>).</summary>
public sealed record RecoverySettings(RecoveryRule Residents, RecoveryRule Staff, RecoveryRule Owner);

/// <summary>Whether the owner lets people sign up; it takes effect only while the legal-page gate is open (see <see cref="Guests.SignUpRules.IsOpen"/>).</summary>
public sealed record SignUpSettings(bool Open = false);

/// <summary>One legal page's text as the operator wrote it (Markdown), how many times it was saved and when.</summary>
public sealed record LegalText(string Markdown, int Version, DateTimeOffset Saved);

/// <summary>
/// The operator's own legal pages. <see cref="Texts"/>: language ("en") to page id ("terms", "privacy", "cookies", "contact") to
/// text; null or empty means none written. GridGate ships no wording. <see cref="OperatorResponsible"/> is the operator's tick that they
/// are responsible for the texts; <see cref="ResponsibleSince"/> is when it was ticked. <see cref="HighestVersion"/>: page id to the
/// highest version that page has ever had in any language, kept when records go (a deleted language), so a number is never used
/// twice; null or a missing page reads as the highest of its records (a file from before plan 16b).
/// </summary>
public sealed record LegalSettings(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, LegalText>>? Texts = null,
    bool OperatorResponsible = false,
    DateTimeOffset? ResponsibleSince = null,
    IReadOnlyDictionary<string, int>? HighestVersion = null);

/// <summary>
/// The owner's wording for the grid's emails: language ("en") to template id to text, only where it differs from the default
/// (<see cref="Email.EmailTemplates"/>); null or empty means every template uses its default.
/// </summary>
public sealed record EmailSettings(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, Email.EmailTemplateText>>? Templates = null);

/// <summary>
/// Protection of the sign-up form against bots. <see cref="Difficulty"/> is the proof-of-work's largest number to try
/// (ALTCHA <c>maxnumber</c>, 1 000 to 1 000 000); <see cref="PerHourPerIp"/> is 1 to 1 000. <see cref="AdminApproval"/>: when email
/// isn't set up, new sign-ups wait for staff approval. <see cref="Approvers"/>: which staff may approve or deny a sign-up.
/// </summary>
public sealed record BotSettings(
    bool Honeypot = true,
    bool RateLimit = true,
    int PerHourPerIp = 5,
    bool ProofOfWork = true,
    int Difficulty = 50_000,
    bool ImageCaptcha = false,
    bool AdminApproval = false,
    SignUpApprovers Approvers = SignUpApprovers.OwnerAndAdmins);

/// <summary>
/// Inventory backups (IAR) for residents (spec 2026-11-11-iar-backups-design §8). Both switches start off: the owner sets the
/// limits first. Sizes are in MB, the unpacked limit of an upload is <see cref="MaxUploadMb"/> × <see cref="UnpackFactor"/>,
/// counts per day are per account over a rolling 24 hours, <see cref="JobsAtOnce"/> is for the whole grid, and
/// <see cref="MinFreeDiskGb"/> is the free space below which new jobs are refused. A resident's own overrides replace some of
/// these (see <c>IarRights.Effective</c>).
/// </summary>
public sealed record IarSettings(
    bool DownloadsOn = false,
    bool UploadsOn = false,
    int MaxUploadMb = 500,
    int UnpackFactor = 4,
    int MaxUploadItems = 20000,
    int MaxDownloadMb = 2048,
    int DownloadsPerDay = 2,
    int UploadsPerDay = 2,
    int JobsAtOnce = 1,
    int DownloadKbPerSecond = 2048,
    int LinkHours = 24,
    int MinFreeDiskGb = 5);

/// <summary>Which staff may approve or deny a sign-up (spec 2026-11-01-sign-up-approval-design §6).</summary>
public enum SignUpApprovers { Owner, OwnerAndAdmins, OwnerAdminsAndSupervisors }

/// <summary>
/// Which languages the resident web pages are offered in. <see cref="Offered"/> is the owner's choice from
/// <see cref="Core.Languages.BuiltIn"/>; <see cref="Default"/> is the language used when a visitor's browser asks for none on the list.
/// </summary>
public sealed record LanguageSettings
{
    public IReadOnlyList<string> Offered { get; init; } = Core.Languages.BuiltIn;
    public string Default { get; init; } = Core.Languages.English;

    /// <summary>The offered languages that are built in or among <c>added</c> (the owner's own), always including the default; a missing list reads as all of them.</summary>
    public IReadOnlyList<string> OfferedOrDefault(IReadOnlyCollection<string>? added = null)
    {
        added ??= [];
        var known = (Offered ?? Core.Languages.BuiltIn).Where(c => Core.Languages.BuiltIn.Contains(c) || added.Contains(c)).Distinct().ToList();
        if (!known.Contains(Default))
            known.Add(Default);
        return known;
    }
}

public sealed record LauncherSettings
{
    public const int CurrentSchema = 7;

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
    /// <summary>The money service's own HTTPS port (money design §7.2).</summary>
    public int MoneyPort { get; init; } = 8000;

    public required DatabaseSecrets Db { get; init; }
    public required ConsoleSecrets Console { get; init; }
    public required string MachineId { get; init; }
    public string? SetupToken { get; init; }

    /// <summary>The physics engine a program without its own uses: "ubODE", "BulletSim" or "BulletSimThreaded".</summary>
    public string DefaultPhysics { get; init; } = "ubODE";
    /// <summary>Whether a program without its own setting runs scripts.</summary>
    public bool DefaultScripts { get; init; } = true;
    /// <summary>The landing regions; null until the owner saves them (read as automatic, no fallbacks).</summary>
    public LandingSettings? Landing { get; init; }

    public SetupState Setup { get; init; } = new();
    private readonly SecuritySettings _security = new();

    /// <summary>Sign-in security; a missing or null value (a hand edit) reads as the defaults.</summary>
    [AllowNull]
    public SecuritySettings Security { get => _security; init => _security = value ?? new(); }

    /// <summary>The password-recovery rules; null until the owner chooses a level (read as all off).</summary>
    public RecoverySettings? Recovery { get; init; }

    /// <summary>The owner's sign-up switch; null until the owner sets it (read as closed).</summary>
    public SignUpSettings? SignUp { get; init; }

    private readonly LegalSettings _legal = new();

    /// <summary>The operator's legal pages; a missing or null value reads as none written.</summary>
    [AllowNull]
    public LegalSettings Legal { get => _legal; init => _legal = value ?? new(); }

    private readonly EmailSettings _email = new();

    /// <summary>The owner's email wording; a missing or null value reads as every default.</summary>
    [AllowNull]
    public EmailSettings Email { get => _email; init => _email = value ?? new(); }

    private readonly LanguageSettings _languages = new();

    /// <summary>The languages the web pages are offered in; a missing or null value reads as the defaults.</summary>
    [AllowNull]
    public LanguageSettings Languages { get => _languages; init => _languages = value ?? new(); }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoWording = new Dictionary<string, IReadOnlyDictionary<string, string>>();
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _wording = NoWording;

    /// <summary>The owner's own wording for some resident-page texts: language code, then text key, then the text. A missing or null value reads as none.</summary>
    [AllowNull]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Wording { get => _wording; init => _wording = Clean(value); }

    // A hand-edited file may hold a null language or a null/blank text; those read as no wording rather than failing every page.
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Clean(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? value)
    {
        if (value is null)
            return NoWording;
        Dictionary<string, IReadOnlyDictionary<string, string>> clean = new(StringComparer.Ordinal);
        foreach (var (lang, texts) in value)
        {
            if (lang is null || texts is null)
                continue;
            var kept = texts.Where(t => t.Key is not null && !string.IsNullOrWhiteSpace(t.Value)).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
            if (kept.Count > 0)
                clean[lang] = kept;
        }
        return clean;
    }

    private readonly BotSettings _bots = new();

    /// <summary>Bot protection for sign-up; a missing or null value reads as the defaults.</summary>
    [AllowNull]
    public BotSettings Bots { get => _bots; init => _bots = value ?? new(); }

    private readonly IarSettings _iar = new();

    /// <summary>Inventory backups for residents; a missing or null value reads as the defaults (both switches off).</summary>
    [AllowNull]
    public IarSettings Iar { get => _iar; init => _iar = value ?? new(); }
    public List<ProgramSettings> Programs { get; init; } = new();
    public List<RegionSettings> Regions { get; init; } = new();
    /// <summary>Region types made from library files, in the order they were added; a missing list reads as empty.</summary>
    public List<CustomRegionType> CustomTypes { get; init; } = new();

    /// <summary>Schema 1's "region"; <see cref="SettingsStore"/> migrates it and it is never written again.</summary>
    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyRegionV1? LegacyRegion { get; init; }

    private readonly MoneySettings _money = new();

    /// <summary>The grid's money settings; a missing or null value reads as the defaults (money Off).</summary>
    [AllowNull]
    public MoneySettings Money { get => _money; init => _money = value ?? new(); }

    public Dictionary<string, Dictionary<string, string>> Modules { get; init; } = new();
    public List<IniOverride> Overrides { get; init; } = new();
}
