using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core;

/// <summary>A language the grid owner added: its code, the wording of the web pages and of the emails, and when it was saved.</summary>
public sealed record OwnerLanguage(
    string Code,
    IReadOnlyDictionary<string, string> Page,
    IReadOnlyDictionary<string, string> Email,
    DateTimeOffset Saved);

/// <summary>
/// The languages the grid owner added, and their translations (plan 16b): one file per language, <c>&lt;folder&gt;/&lt;code&gt;.json</c>,
/// written atomically (<see cref="AtomicFile"/>). The files are cached in memory and read again when one changes (its size or time).
/// A file that isn't valid JSON is moved aside as <c>&lt;code&gt;.json.damaged-&lt;utc yyyyMMddHHmmss&gt;</c>, with a warning, and reads
/// as absent. The file name comes from the code only, and the code is always one <see cref="CodeFor"/> accepts. Safe to call from any thread.
/// </summary>
public sealed class OwnerLanguages(string folder, TimeProvider time, ILogger<OwnerLanguages>? log = null)
{
    private sealed record Cached(long Length, DateTime Written, OwnerLanguage Language);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    private IReadOnlyList<string>? _codes;
    private DateTimeOffset _scanned;

    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(1);

    // Seams for tests: the folder listing and the file removal.
    internal Func<string, IEnumerable<string>> ListFiles { get; set; } = dir => Directory.EnumerateFiles(dir, "*.json");
    internal Action<string> RemoveFile { get; set; } = File.Delete;

    /// <summary>
    /// The normalised culture name for what the owner typed ("IT" gives "it", "pt-pt" gives "pt-PT"), or null when the system doesn't
    /// know it, it isn't plainly a language tag, or it is one of <see cref="Languages.BuiltIn"/>.
    /// </summary>
    public static string? CodeFor(string? typed)
    {
        typed = typed?.Trim();
        if (Languages.Match(typed, [typed?.ToLowerInvariant() ?? ""]) is null)
            return null;
        string name;
        try
        {
            name = CultureInfo.GetCultureInfo(typed!, predefinedOnly: true).Name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
        return name.Length == 0 || Languages.BuiltIn.Contains(name, StringComparer.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>
    /// The codes of the saved languages, in order. The folder is looked at again at most once a second (<see cref="Save"/> and
    /// <see cref="Delete"/> refresh it at once). A folder that can't be read gives an empty list and a warning, never an exception.
    /// </summary>
    public IReadOnlyList<string> Codes()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_codes is not null && now - _scanned < ScanEvery && now >= _scanned)
                return _codes;
            _scanned = now;
            try
            {
                List<string> found = [];
                if (Directory.Exists(folder))
                    foreach (var file in ListFiles(folder))
                    {
                        var code = Path.GetFileNameWithoutExtension(file);
                        if (CodeFor(code) == code && Load(code) is not null)
                            found.Add(code);
                    }
                foreach (var stale in _cache.Keys.Where(k => !found.Contains(k)).ToList())
                    _cache.Remove(stale);
                found.Sort(StringComparer.Ordinal);
                return _codes = found;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.LogWarning("The language folder {Folder} could not be read ({Error}); no added languages are listed.", folder, e.GetType().Name);
                return _codes = [];
            }
        }
    }

    /// <summary>The saved language with <paramref name="code"/>, or null when there is none.</summary>
    public OwnerLanguage? Get(string code)
    {
        if (CodeFor(code) is not { } name)
            return null;
        lock (_gate)
            return Load(name);
    }

    /// <summary>Saves <paramref name="language"/>, replacing an earlier one with the same code.</summary>
    /// <exception cref="ArgumentException">The code isn't a language the system knows, or it is a built-in one.</exception>
    public void Save(OwnerLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (CodeFor(language.Code) is not { } name)
            throw new ArgumentException("That is not a language that can be added.", nameof(language));
        var stored = language with { Code = name, Saved = language.Saved == default ? time.GetUtcNow() : language.Saved };
        lock (_gate)
        {
            Directory.CreateDirectory(folder);
            AtomicFile.WriteAllText(PathOf(name), JsonSerializer.Serialize(stored, Options));
            _cache.Remove(name);
            _codes = null;
        }
    }

    /// <summary>Removes the saved language with <paramref name="code"/>; false when there was none or the file could not be removed (a warning is logged).</summary>
    public bool Delete(string code)
    {
        if (CodeFor(code) is not { } name)
            return false;
        lock (_gate)
        {
            _cache.Remove(name);
            _codes = null;
            var path = PathOf(name);
            try
            {
                if (!File.Exists(path))
                    return false;
                RemoveFile(path);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.LogWarning("The language file {Path} could not be removed ({Error}).", path, e.GetType().Name);
                return false;
            }
        }
    }

    private string PathOf(string name) => Path.Combine(folder, name + ".json");

    // Called with the lock held.
    private OwnerLanguage? Load(string name)
    {
        var path = PathOf(name);
        FileInfo info = new(path);
        if (!info.Exists)
        {
            _cache.Remove(name);
            return null;
        }
        if (_cache.TryGetValue(name, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc)
            return hit.Language;

        string json;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            json = reader.ReadToEnd();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _cache.Remove(name);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("The language file {Path} could not be read ({Error}); it reads as absent.", path, e.GetType().Name);
            return null;
        }

        OwnerLanguage? language = null;
        try
        {
            language = JsonSerializer.Deserialize<OwnerLanguage>(json, Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
        }
        if (language is null || language.Page is null || language.Email is null
            || !string.Equals(language.Code, name, StringComparison.Ordinal))
        {
            SetAside(path);
            _cache.Remove(name);
            return null;
        }
        // A null wording in the file reads as absent.
        language = language with
        {
            Page = language.Page.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value),
            Email = language.Email.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value),
        };
        _cache[name] = new Cached(info.Length, info.LastWriteTimeUtc, language);
        return language;
    }

    private void SetAside(string path)
    {
        var aside = path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        try
        {
            File.Move(path, aside, overwrite: true);
            log?.LogWarning("The language file {Path} could not be read and was set aside as {Aside}.", path, Path.GetFileName(aside));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("The language file {Path} could not be read and could not be set aside ({Error}).", path, e.GetType().Name);
        }
    }
}
