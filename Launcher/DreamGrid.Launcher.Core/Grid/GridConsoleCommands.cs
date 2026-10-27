using System.Globalization;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.OpenSim;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>The console dialogues the grid controller runs against Robust and the region.</summary>
public static partial class GridConsoleCommands
{
    private const int MaxPromptAnswers = 5;

    /// <summary>
    /// Rejects owner input the console would split or rewrite: CommandConsole's parser splits on whitespace,
    /// groups double quotes, and merges a word starting with "--" (an option) with what follows.
    /// Robust refuses ' ', '@', '.' and ':' in names.
    /// </summary>
    public static void ValidateOwner(string first, string last, string password, string? email)
    {
        ValidateName(first, nameof(first));
        ValidateName(last, nameof(last));
        if (string.IsNullOrEmpty(password) || password.Any(c => char.IsWhiteSpace(c) || c == '"') || password.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("The password must not be empty, contain spaces or double quotes, or start with \"--\".", nameof(password));
        if (email is not null && (email.Any(c => char.IsWhiteSpace(c) || c == '"') || email.StartsWith("--", StringComparison.Ordinal)))
            throw new ArgumentException("The e-mail address must not contain spaces or double quotes, or start with \"--\".", nameof(email));
    }

    private static void ValidateName(string name, string param)
    {
        if (string.IsNullOrEmpty(name) || name.StartsWith("--", StringComparison.Ordinal)
            || name.Any(c => char.IsWhiteSpace(c) || c is '@' or '.' or ':' or '"'))
            throw new ArgumentException("An avatar name part must be non-empty and contain no spaces, '@', '.', ':' or '\"'.", param);
    }

    /// <summary>
    /// <c>create user</c> with all eight arguments (an empty model means the default appearance), so Robust should
    /// not prompt. A line the server marks as an interactive prompt is still answered with an empty line (its
    /// default). Returns the console transcript.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CreateUserAsync(RestConsoleClient console, string first, string last,
        string password, string? email, Guid id, TimeSpan settle, CancellationToken ct)
    {
        var transcript = new List<string>();
        var output = await console.RunLinesAsync($"create user {first} {last} {password} \"{email ?? ""}\" {id} \"\"", settle, ct);
        transcript.AddRange(output.Select(l => l.Text));
        for (var i = 0; i < MaxPromptAnswers && output.Count > 0 && output[^1].IsPrompt; i++)
        {
            output = await console.RunLinesAsync("", settle, ct);
            transcript.AddRange(output.Select(l => l.Text));
        }
        return transcript;
    }

    /// <summary>
    /// The region's <c>login status</c> (AccessModule): true/false, or null when the reply is not recognised.
    /// AccessModule answers "status" with "Login in NAME are enabled|disabled"; the "Logins are ... for region"
    /// wording is its reply to enable/disable and is accepted too.
    /// </summary>
    public static async Task<bool?> LoginsEnabledAsync(RestConsoleClient console, TimeSpan settle, CancellationToken ct) =>
        ParseLoginsEnabled(await console.RunAsync("login status", settle, ct));

    /// <summary>The first enabled/disabled answer in <c>login status</c> output (either wording), or null when none.</summary>
    public static bool? ParseLoginsEnabled(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Contains("Logins are enabled for region", StringComparison.Ordinal)) return true;
            if (line.Contains("Logins are disabled for region", StringComparison.Ordinal)) return false;
            if (line.StartsWith("Login in ", StringComparison.Ordinal))
            {
                if (line.EndsWith(" are enabled", StringComparison.Ordinal)) return true;
                if (line.EndsWith(" are disabled", StringComparison.Ordinal)) return false;
            }
        }
        return null;
    }

    /// <summary>
    /// Every "Login in NAME are enabled|disabled" line of <c>login status</c> (RegionReadyModule prints one per region), by
    /// region name.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> ParseLoginStatus(IEnumerable<string> lines)
    {
        var status = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (!line.StartsWith("Login in ", StringComparison.Ordinal)) continue;
            foreach (var (suffix, enabled) in new[] { (" are enabled", true), (" are disabled", false) })
                if (line.EndsWith(suffix, StringComparison.Ordinal))
                    status[line["Login in ".Length..^suffix.Length]] = enabled;
        }
        return status;
    }

    public static async Task<IReadOnlyDictionary<string, bool>> LoginStatusAsync(RestConsoleClient console, TimeSpan settle, CancellationToken ct) =>
        ParseLoginStatus(await console.RunAsync("login status", settle, ct));

    /// <summary>
    /// The region IDs in <c>show regions</c>' table, or null when the output holds none (no table: the answer is
    /// unknown). IDs, not names: the name column pads to 20 characters and names may hold spaces.
    /// </summary>
    public static IReadOnlyList<Guid>? ParseShowRegions(IEnumerable<string> lines)
    {
        var ids = lines.SelectMany(l => RegionIdPattern().Matches(l)).Select(m => Guid.Parse(m.Value)).ToList();
        return ids.Count == 0 ? null : ids;
    }

    public static async Task<IReadOnlyList<Guid>?> LoadedRegionsAsync(RestConsoleClient console, TimeSpan settle, CancellationToken ct) =>
        ParseShowRegions(await console.RunAsync("show regions", settle, ct));

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex RegionIdPattern();

    /// <summary>
    /// True when <c>terrain stats</c> (TerrainModule, which logs load results only to its log file) shows island
    /// relief. The flat default terrain a failed load leaves behind has max == min. The console thread may still be
    /// finishing <c>terrain load</c> when the first <c>terrain stats</c> is sent, so the command is repeated until a
    /// stats line shows up or <see cref="TerrainStatsWait"/> has passed; only a stats line decides. The wait outlasts
    /// the console client's recovery from a read the server dropped (RestConsoleClient.LostReadAfter, 30 s).
    /// </summary>
    public static async Task<bool> IslandTerrainLoadedAsync(RestConsoleClient console, TimeSpan settle, TimeProvider time, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + TerrainStatsWait;
        while (true)
        {
            foreach (var line in await console.RunAsync("terrain stats", settle, ct))
            {
                var m = StatsPattern().Match(line);
                if (!m.Success) continue;
                if (!TryParse(m.Groups[1].Value, out var max) || !TryParse(m.Groups[2].Value, out var min)) return false;
                return max - min >= 10;
            }
            if (time.GetUtcNow() >= deadline) return false;
        }
    }

    private static readonly TimeSpan TerrainStatsWait = TimeSpan.FromSeconds(60);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(@"max/min/avg/sum:\s*([^/]+)/([^/]+)/")]
    private static partial Regex StatsPattern();
}
