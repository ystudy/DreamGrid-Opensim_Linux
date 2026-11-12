using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// One resident's inventory backup switches and own limits (spec 2026-11-11-iar-backups-design §8). Both switches start on: the
/// grid's switch decides unless staff switched this person off. A null limit uses the grid's.
/// </summary>
public sealed record IarPersonRights(Guid Person, bool DownloadOff, bool UploadOff, int? MaxUploadMb, int? MaxDownloadMb,
    int? DownloadsPerDay, int? UploadsPerDay, int? DownloadKbPerSecond)
{
    /// <summary>All allowed, the grid's limits.</summary>
    public static IarPersonRights Default(Guid person) => new(person, false, false, null, null, null, null, null);

    public bool IsDefault => this == Default(Person);
}

/// <summary>What <c>iar-rights.json</c> holds: only the people who differ from the default.</summary>
public sealed class IarRightsData
{
    public List<IarPersonRights> People { get; set; } = [];
}

/// <summary>
/// Each resident's inventory backup switches and limits (the panel on a person's page). Kept in <c>file</c>
/// (<c>&lt;data&gt;/guests/iar-rights.json</c>). A missing or damaged file means everyone has the defaults (the grid's switches and
/// limits decide); <see cref="JsonStateFile{T}"/> sets a damaged one aside. Safe to call from any thread.
/// </summary>
public sealed class IarRights
{
    private readonly JsonStateFile<IarRightsData> _file;
    private readonly Lock _gate = new();
    private readonly IarRightsData _data;

    public IarRights(string file, ILogger<IarRights>? log = null)
    {
        _file = new JsonStateFile<IarRightsData>(file, () => new IarRightsData(), log);
        _data = _file.Load();
    }

    /// <summary>The person's rights; the defaults for anyone not on file.</summary>
    public IarPersonRights For(Guid person)
    {
        lock (_gate) return _data.People.FirstOrDefault(p => p.Person == person) ?? IarPersonRights.Default(person);
    }

    /// <summary>Replaces the person's rights; the defaults are not kept on file.</summary>
    public void Set(IarPersonRights rights)
    {
        lock (_gate)
        {
            _data.People.RemoveAll(p => p.Person == rights.Person);
            if (!rights.IsDefault) _data.People.Add(rights);
            _file.Save(_data);
        }
    }

    /// <summary>The grid's settings with the person's own limits in place of the grid's.</summary>
    public static IarSettings Effective(IarSettings grid, IarPersonRights person) => grid with
    {
        MaxUploadMb = person.MaxUploadMb ?? grid.MaxUploadMb,
        MaxDownloadMb = person.MaxDownloadMb ?? grid.MaxDownloadMb,
        DownloadsPerDay = person.DownloadsPerDay ?? grid.DownloadsPerDay,
        UploadsPerDay = person.UploadsPerDay ?? grid.UploadsPerDay,
        DownloadKbPerSecond = person.DownloadKbPerSecond ?? grid.DownloadKbPerSecond,
    };
}
