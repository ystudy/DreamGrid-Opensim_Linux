using System.Globalization;
using Microsoft.Extensions.Logging;

namespace GridGate.Launcher.Core;

/// <summary>
/// The language each resident chose for the web pages and emails, by account id (plan 16a). Kept in <c>file</c>
/// (<c>&lt;data&gt;/guests/languages.json</c>) through <see cref="JsonStateFile{T}"/>: read once at start, held in memory, written
/// atomically on every change. A file that isn't valid JSON is moved aside with a warning and reads as empty; a save that fails is
/// logged once and the choices are kept in memory only. Safe to call from any thread.
/// </summary>
public sealed class ResidentLanguages
{
    private readonly JsonStateFile<Dictionary<Guid, string>> _file;
    private readonly Dictionary<Guid, string> _chosen;
    private readonly Lock _gate = new();

    public ResidentLanguages(string file, ILogger<ResidentLanguages>? log = null)
    {
        _file = new JsonStateFile<Dictionary<Guid, string>>(file, () => [], log);
        _chosen = _file.Load();
    }

    /// <summary>The language <paramref name="principal"/> chose, or null when they haven't.</summary>
    public string? Get(Guid principal)
    {
        lock (_gate)
            return _chosen.GetValueOrDefault(principal);
    }

    /// <summary>
    /// Records <paramref name="code"/> as the language of <paramref name="principal"/>, replacing an earlier one. Any language the
    /// system knows is accepted, not only the built-in ones: the owner can add more.
    /// </summary>
    /// <exception cref="ArgumentException">The code isn't a language tag the system knows.</exception>
    public void Set(Guid principal, string code)
    {
        var lower = code?.Trim().ToLowerInvariant();
        if (lower is null || Languages.Match(lower, [lower]) is null || !IsKnown(lower))
            throw new ArgumentException("That is not a language.", nameof(code));
        lock (_gate)
        {
            _chosen[principal] = lower;
            _file.Save(_chosen);
        }
    }

    /// <summary>Forgets the language <paramref name="principal"/> chose, if any (for instance when their account is deleted).</summary>
    public void Remove(Guid principal)
    {
        lock (_gate)
            if (_chosen.Remove(principal))
                _file.Save(_chosen);
    }

    /// <summary>How many residents chose <paramref name="code"/>.</summary>
    public int CountUsing(string code)
    {
        lock (_gate)
            return _chosen.Values.Count(v => string.Equals(v, code, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKnown(string code)
    {
        try
        {
            CultureInfo.GetCultureInfo(code, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
