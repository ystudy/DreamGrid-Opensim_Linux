using Microsoft.Extensions.Logging;

namespace GridGate.Launcher.Core.Groups;

/// <summary>What <c>group-joins.json</c> holds. Member IDs are kept lower-case.</summary>
public sealed record GroupJoinsState(Guid? DefaultGroup, Dictionary<Guid, HashSet<string>> Seen);

/// <summary>
/// The default group, and per group everyone the launcher knows has been in it: added by the launcher, found in it by
/// "Add everyone now", or in it when it was made the default. The list only grows (a group's list goes when the group does), so
/// someone who leaves is never put back automatically. Kept in <c>&lt;data&gt;/guests/group-joins.json</c>; a change is kept in
/// memory only when it was saved, and a failed save returns false. Safe to call from any thread.
/// </summary>
public sealed class GroupJoins
{
    private readonly JsonStateFile<GroupJoinsState> _file;
    private readonly Lock _gate = new();
    private GroupJoinsState _state;

    public GroupJoins(string path, ILogger<GroupJoins>? log)
    {
        _file = new JsonStateFile<GroupJoinsState>(path, Empty, log);
        _state = _file.Load();
    }

    private static GroupJoinsState Empty() => new(null, []);

    public Guid? Default()
    {
        lock (_gate) return _state.DefaultGroup;
    }

    public bool Has(Guid group, string principalId)
    {
        lock (_gate) return _state.Seen.TryGetValue(group, out var ids) && ids.Contains(Key(principalId));
    }

    /// <summary>Makes <paramref name="group"/> the default and remembers its current members, in one save.</summary>
    public bool SetDefault(Guid group, IEnumerable<string> currentMembers) =>
        Change(s => Add(s, group, currentMembers) with { DefaultGroup = group });

    public bool ClearDefault() => Change(s => s with { DefaultGroup = null });

    public bool Remember(Guid group, IEnumerable<string> principalIds) => Change(s => Add(s, group, principalIds));

    /// <summary>Forgets a deleted group's list, and the default when it was that group.</summary>
    public bool GroupDeleted(Guid group) => Change(s =>
    {
        var seen = Copy(s.Seen);
        seen.Remove(group);
        return new GroupJoinsState(s.DefaultGroup == group ? null : s.DefaultGroup, seen);
    });

    private bool Change(Func<GroupJoinsState, GroupJoinsState> change)
    {
        lock (_gate)
        {
            var next = change(_state);
            if (!_file.Save(next)) return false;
            _state = next;
            return true;
        }
    }

    private static GroupJoinsState Add(GroupJoinsState s, Guid group, IEnumerable<string> ids)
    {
        var seen = Copy(s.Seen);
        if (!seen.TryGetValue(group, out var set)) seen[group] = set = [];
        foreach (var id in ids) set.Add(Key(id));
        return s with { Seen = seen };
    }

    private static Dictionary<Guid, HashSet<string>> Copy(Dictionary<Guid, HashSet<string>> seen) =>
        seen.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value));

    private static string Key(string principalId) => principalId.Trim().ToLowerInvariant();
}
