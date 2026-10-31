using DreamGrid.Launcher.Core;

namespace DreamGrid.Launcher.Guests;

/// <summary>One guest's agreement to one version of the Terms: who, which version, when, and from which address.</summary>
public sealed record TermsAcceptance(Guid Principal, int Version, DateTimeOffset At, string? Ip);

/// <summary>
/// Who has accepted which version of the operator's Terms. Every acceptance is kept, newest last, so the record shows what each
/// guest agreed to and when. Kept in <c>file</c> (<c>&lt;data&gt;/guests/terms.json</c>); a missing or damaged file means nobody
/// has accepted, so everyone is asked again (<see cref="JsonStateFile{T}"/>). Safe to call from any thread.
/// </summary>
public sealed class TermsAcceptances
{
    private readonly JsonStateFile<List<TermsAcceptance>> _file;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<TermsAcceptance> _all;

    public TermsAcceptances(string file, TimeProvider time, ILogger<TermsAcceptances>? log = null)
    {
        _time = time;
        _file = new JsonStateFile<List<TermsAcceptance>>(file, () => [], log);
        _all = _file.Load();
    }

    /// <summary>Whether the person has accepted exactly this version.</summary>
    public bool HasAccepted(Guid principal, int version)
    {
        lock (_gate) return _all.Any(a => a.Principal == principal && a.Version == version);
    }

    /// <summary>Records that the person accepted this version now, from <paramref name="ip"/>.</summary>
    public void Accept(Guid principal, int version, string? ip)
    {
        lock (_gate)
        {
            _all.Add(new TermsAcceptance(principal, version, _time.GetUtcNow(), ip));
            _file.Save(_all);
        }
    }

    /// <summary>Everything the person has accepted, oldest first.</summary>
    public IReadOnlyList<TermsAcceptance> Of(Guid principal)
    {
        lock (_gate) return _all.Where(a => a.Principal == principal).ToList();
    }
}
