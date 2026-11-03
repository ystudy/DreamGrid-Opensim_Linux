using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>An old avatar name kept from others until <see cref="Until"/>, and whose it was.</summary>
public sealed record HeldName(string Name, Guid FormerOwner, DateTimeOffset Until);

/// <summary>
/// Names given up in a rename, kept for <see cref="HoldFor"/> so nobody else can take one and pose as its former owner. A held
/// name counts as taken at sign-up and in the People page's edit. Kept in <c>file</c> (<c>&lt;data&gt;/guests/held-names.json</c>);
/// a missing or damaged file holds nothing (<see cref="JsonStateFile{T}"/>). Names compare without case. Safe from any thread.
/// </summary>
public sealed class HeldNames
{
    public static readonly TimeSpan HoldFor = TimeSpan.FromDays(30);

    private readonly JsonStateFile<List<HeldName>> _file;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private List<HeldName> _all;

    public HeldNames(string file, TimeProvider time, ILogger<HeldNames>? log = null)
    {
        _time = time;
        _file = new JsonStateFile<List<HeldName>>(file, () => [], log);
        _all = _file.Load();
    }

    /// <summary>Holds <paramref name="fullName"/> ("First Last") for <see cref="HoldFor"/> from now; false when it couldn't be saved.</summary>
    public bool Hold(string fullName, Guid formerOwner)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            List<HeldName> next = [.. _all.Where(h => h.Until > now && !Same(h.Name, fullName)), new HeldName(fullName.Trim(), formerOwner, now + HoldFor)];
            return Swap(next);
        }
    }

    /// <summary>Whether <paramref name="fullName"/> is held now.</summary>
    public bool IsHeld(string fullName)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return _all.Any(h => h.Until > now && Same(h.Name, fullName));
        }
    }

    /// <summary>Drops every name <paramref name="formerOwner"/> gave up (their account is deleted); false when it couldn't be saved.</summary>
    public bool Release(Guid formerOwner)
    {
        lock (_gate)
        {
            if (!_all.Any(h => h.FormerOwner == formerOwner)) return true;
            return Swap([.. _all.Where(h => h.FormerOwner != formerOwner)]);
        }
    }

    private bool Swap(List<HeldName> next)
    {
        if (!_file.Save(next)) return false;
        _all = next;
        return true;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
