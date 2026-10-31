using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Modules;

namespace DreamGrid.Launcher.Core.Settings;

/// <summary>Checks Advanced Settings values before anything is saved. Error keys are LauncherSettings property names.</summary>
public static partial class SettingsValidator
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;

    /// <summary>The port fields in page order, with the names the Advanced Settings pages show.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> PortFields =
    [
        ("WebPort", "Web port (this website)"),
        ("RobustPublicPort", "Robust public port"),
        ("RobustPrivatePort", "Robust private port"),
        ("RegionPort", "First region port"),
        ("MariaDbPort", "MariaDB port"),
        ("RobustConsolePort", "Robust console port"),
        ("RegionConsolePort", "First region console port"),
    ];

    /// <summary>
    /// Every port 1024–65535 and all distinct; the hostname empty (automatic), a DNS name or an IPv4 address. No port
    /// but the two "first region" ones may be one a region already holds (<paramref name="assigned"/>: port to region).
    /// </summary>
    public static ValidationResult Network(string? hostname, int web, int robustPublic, int robustPrivate, int region,
        int mariaDb, int robustConsole, int regionConsole, IReadOnlyDictionary<int, string>? assigned = null)
    {
        var errors = new Dictionary<string, string>();
        if (HostnameError(hostname) is { } hostError) errors["Hostname"] = hostError;

        int[] ports = [web, robustPublic, robustPrivate, region, mariaDb, robustConsole, regionConsole];
        for (var i = 0; i < ports.Length; i++)
        {
            var key = PortFields[i].Key;
            if (ports[i] is < MinPort or > MaxPort)
            {
                errors[key] = $"Use a port from {MinPort} to {MaxPort}.";
                continue;
            }
            var others = Enumerable.Range(0, ports.Length).Where(j => j != i && ports[j] == ports[i]).Select(j => PortFields[j].Label).ToList();
            if (others.Count > 0)
                errors[key] = $"Port {ports[i]} is also used for: {string.Join(", ", others)}. Each port must be different.";
            else if (key is not ("RegionPort" or "RegionConsolePort") && assigned?.TryGetValue(ports[i], out var owner) == true)
                errors[key] = $"Port {ports[i]} is used by region {owner}.";
        }
        return new ValidationResult(errors);
    }

    /// <summary>The supervisor level at least 1, the admin level above it and at most 255 (OpenSim's god level range).</summary>
    public static ValidationResult Security(SecuritySettings security)
    {
        var errors = new Dictionary<string, string>();
        if (security.SupervisorLevel < 1)
            errors["SupervisorLevel"] = "Use a supervisor level of 1 or more.";
        if (security.AdminLevel > 255)
            errors["AdminLevel"] = "Use an admin level of 255 or less.";
        else if (security.AdminLevel <= security.SupervisorLevel)
            errors["AdminLevel"] = "The admin level must be higher than the supervisor level.";
        return new ValidationResult(errors);
    }

    public const int MinDifficulty = 1_000;
    public const int MaxDifficulty = 1_000_000;
    public const int MinPerHourPerIp = 1;
    public const int MaxPerHourPerIp = 1_000;

    /// <summary>The proof-of-work difficulty 1 000 to 1 000 000 and the sign-ups per hour per address 1 to 1 000.</summary>
    public static ValidationResult Bots(BotSettings bots)
    {
        var errors = new Dictionary<string, string>();
        if (bots.Difficulty is < MinDifficulty or > MaxDifficulty)
            errors["Difficulty"] = "Use a difficulty from 1000 to 1000000.";
        if (bots.PerHourPerIp is < MinPerHourPerIp or > MaxPerHourPerIp)
            errors["PerHourPerIp"] = "Use a number from 1 to 1000 sign-ups per hour.";
        return new ValidationResult(errors);
    }

    /// <summary>Null when <paramref name="hostname"/> is empty (automatic), a DNS name or an IPv4 address.</summary>
    public static string? HostnameError(string? hostname)
    {
        if (string.IsNullOrEmpty(hostname)) return null;
        const string Plain = "Use a name like world.example.com or an address like 192.168.1.20, with no spaces. Leave it empty to use the detected address.";
        if (hostname.Any(char.IsWhiteSpace)) return Plain;
        // All digits and dots: it must be a whole IPv4 address (DNS names cannot end in a numeric label).
        if (hostname.All(c => c == '.' || char.IsAsciiDigit(c)))
            return IsIpv4(hostname) ? null : Plain;
        if (hostname.Length > 253) return Plain;
        return hostname.Split('.').All(DnsLabel().IsMatch) ? null : Plain;
    }

    // Nini (which OpenSim reads the INIs with) treats ; and # as comments and expands ${Section|Key}.
    private const string ForbiddenInGridName = "\";#$[]{}=\\";

    /// <summary>Null when the trimmed name is 1–64 characters with no control characters and none of <c>" ; # $ [ ] { } = \</c>.</summary>
    public static string? GridNameError(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length is >= 1 and <= 64 && !trimmed.Any(c => char.IsControl(c) || ForbiddenInGridName.Contains(c))
            ? null
            : "Use 1 to 64 letters, digits, spaces or punctuation, without quotes or the symbols ; # $ [ ] { } = \\";
    }

    private static bool IsIpv4(string s)
    {
        var parts = s.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && int.Parse(p) <= 255);
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex DnsLabel();
}
