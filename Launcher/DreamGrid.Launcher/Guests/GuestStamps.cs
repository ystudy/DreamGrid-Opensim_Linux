using System.Security.Cryptography;
using DreamGrid.Launcher.Core;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Each guest's stamp: a value the guest cookie carries and that changes whenever the person's guest sessions must all end
/// (a suspension). A cookie is good only while its stamp is the person's current one, so <see cref="Rotate"/> takes effect on
/// the guest's next request, not at the next level recheck. A person who never had one has the stamp <c>"0"</c>. Kept in
/// <c>file</c> (<c>&lt;data&gt;/guests/stamps.json</c>) so a restart keeps guests signed in; a missing or damaged file means
/// every stamp is <c>"0"</c> again (<see cref="JsonStateFile{T}"/>). Safe to call from any thread.
/// </summary>
public sealed class GuestStamps
{
    public const string None = "0";

    private readonly JsonStateFile<Dictionary<Guid, string>> _file;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, string> _stamps;

    public GuestStamps(string file, ILogger<GuestStamps>? log = null)
    {
        _file = new JsonStateFile<Dictionary<Guid, string>>(file, () => [], log);
        _stamps = _file.Load();
    }

    /// <summary>The person's current stamp, <c>"0"</c> when none was ever set.</summary>
    public string StampOf(Guid principal)
    {
        lock (_gate) return _stamps.TryGetValue(principal, out var stamp) ? stamp : None;
    }

    /// <summary>Gives the person a new stamp, so no guest cookie issued before is good.</summary>
    public void Rotate(Guid principal)
    {
        lock (_gate)
        {
            _stamps[principal] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _file.Save(_stamps);
        }
    }
}
