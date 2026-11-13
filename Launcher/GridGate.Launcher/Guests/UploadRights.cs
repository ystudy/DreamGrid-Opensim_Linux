using GridGate.Launcher.Core;

namespace GridGate.Launcher.Guests;

/// <summary>One guest who may upload models: who, under what name when staff said so, and since when.</summary>
public sealed record UploadRight(Guid Person, string Name, DateTimeOffset Since);

/// <summary>What <c>upload-rights.json</c> holds.</summary>
public sealed class UploadRightsData
{
    public List<UploadRight> Rights { get; set; } = [];
}

/// <summary>
/// Which guests may upload models (the switch on a person's page). Kept in <c>file</c> (<c>&lt;data&gt;/guests/upload-rights.json</c>);
/// a missing or damaged file means nobody has the right (<see cref="JsonStateFile{T}"/> sets a damaged one aside), so a fault never
/// lets anyone upload. Safe to call from any thread.
/// </summary>
public sealed class UploadRights
{
    private readonly JsonStateFile<UploadRightsData> _file;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly UploadRightsData _data;

    public UploadRights(string file, TimeProvider time, ILogger<UploadRights>? log = null)
    {
        _time = time;
        _file = new JsonStateFile<UploadRightsData>(file, () => new UploadRightsData(), log);
        _data = _file.Load();
    }

    /// <summary>Whether the person may upload models.</summary>
    public bool Has(Guid person)
    {
        lock (_gate) return _data.Rights.Any(r => r.Person == person);
    }

    /// <summary>Lets the person upload models. Does nothing when they already may.</summary>
    public void Grant(Guid person, string name)
    {
        lock (_gate)
        {
            if (_data.Rights.Any(r => r.Person == person)) return;
            _data.Rights.Add(new UploadRight(person, name, _time.GetUtcNow()));
            _file.Save(_data);
        }
    }

    /// <summary>Takes the right away. Does nothing when they don't have it.</summary>
    public void Remove(Guid person)
    {
        lock (_gate)
        {
            if (_data.Rights.RemoveAll(r => r.Person == person) > 0) _file.Save(_data);
        }
    }
}
