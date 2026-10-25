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

public sealed record RegionSettings(
    string Instance,
    string Name,
    Guid Uuid,
    int X = 1000,
    int Y = 1000,
    string Physics = "ubODE",
    string Content = "starter");

/// <summary><see cref="Target"/> is "robust" or "region".</summary>
public sealed record IniOverride(string Target, string Section, string Key, string Value);

public sealed record LauncherSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string GridName { get; init; } = "My World";
    /// <summary>Null means the detected LAN IP.</summary>
    public string? Hostname { get; init; }

    public int WebPort { get; init; } = 8001;
    public int RobustPublicPort { get; init; } = 8002;
    public int RobustPrivatePort { get; init; } = 8003;
    public int RegionPort { get; init; } = 8004;
    public int MariaDbPort { get; init; } = 3307;
    public int RobustConsolePort { get; init; } = 8010;
    public int RegionConsolePort { get; init; } = 8011;

    public required DatabaseSecrets Db { get; init; }
    public required ConsoleSecrets Console { get; init; }
    public required string MachineId { get; init; }
    public string? SetupToken { get; init; }

    public SetupState Setup { get; init; } = new();
    public RegionSettings? Region { get; init; }

    public Dictionary<string, Dictionary<string, string>> Modules { get; init; } = new();
    public List<IniOverride> Overrides { get; init; } = new();
}
