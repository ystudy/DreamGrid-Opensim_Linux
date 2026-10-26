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
        ("RegionPort", "Region port"),
        ("MariaDbPort", "MariaDB port"),
        ("RobustConsolePort", "Robust console port"),
        ("RegionConsolePort", "Region console port"),
    ];

    /// <summary>Every port 1024–65535 and all distinct; the hostname empty (automatic), a DNS name or an IPv4 address.</summary>
    public static ValidationResult Network(string? hostname, int web, int robustPublic, int robustPrivate, int region,
        int mariaDb, int robustConsole, int regionConsole)
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
        }
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

    private static bool IsIpv4(string s)
    {
        var parts = s.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && int.Parse(p) <= 255);
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex DnsLabel();
}
