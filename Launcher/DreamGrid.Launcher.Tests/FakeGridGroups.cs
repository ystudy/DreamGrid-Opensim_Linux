using DreamGrid.Launcher.Core.Groups;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// The grid's group tables in memory, answering as <see cref="GridGroups"/> does, with names from the fake Robust.
/// <see cref="FailAfter"/> makes every call after the first <c>n</c> throw <see cref="GroupsUnavailableException"/>.
/// </summary>
internal sealed class FakeGridGroups(FakeRobustAccounts robust) : IGridGroups
{
    private sealed class Group
    {
        public required Guid Id { get; init; }
        public required string Name { get; set; }
        public string Charter { get; set; } = "";
        public required string Founder { get; init; }
        public bool Open { get; init; }
        public Guid OwnerRole { get; } = Guid.NewGuid();
        // member ID → (role name, is the Owners role)
        public Dictionary<string, List<(string Role, bool Owner)>> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Group> _groups = [];
    private int _calls;
    private int? _failAfter;

    /// <summary>Calls after the first <paramref name="calls"/> (from now) throw; null puts the database back.</summary>
    public void FailAfter(int? calls)
    {
        lock (_gate)
        {
            _calls = 0;
            _failAfter = calls;
        }
    }

    /// <summary>Makes a group as the viewer would: the founder in Everyone and Owners.</summary>
    public Guid AddGroup(string name, Guid founder, bool open = false)
    {
        lock (_gate)
        {
            var g = new Group { Id = Guid.NewGuid(), Name = name, Founder = founder.ToString(), Open = open };
            g.Members[founder.ToString()] = [("Everyone", false), ("Owners", true)];
            _groups[g.Id] = g;
            return g.Id;
        }
    }

    /// <summary>Puts someone in a group directly (as the viewer would), optionally as an owner.</summary>
    public void Join(Guid group, string principalId, bool owner = false)
    {
        lock (_gate)
            _groups[group].Members[principalId] = owner ? [("Everyone", false), ("Owners", true)] : [("Everyone", false)];
    }

    /// <summary>A member leaving in the viewer.</summary>
    public void Leave(Guid group, string principalId)
    {
        lock (_gate) _groups[group].Members.Remove(principalId);
    }

    /// <summary>A group deleted behind the launcher's back.</summary>
    public void Drop(Guid group)
    {
        lock (_gate) _groups.Remove(group);
    }

    public bool IsMember(Guid group, Guid principal)
    {
        lock (_gate) return _groups.TryGetValue(group, out var g) && g.Members.ContainsKey(principal.ToString());
    }

    public bool Exists(Guid group)
    {
        lock (_gate) return _groups.ContainsKey(group);
    }

    public Task<IReadOnlyList<GroupSummary>> ListAsync(CancellationToken ct) => Run<IReadOnlyList<GroupSummary>>(() =>
        _groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupSummary(g.Id, g.Name, NameOf(g.Founder), g.Members.Count, g.Open)).ToList());

    public Task<GroupDetail?> GetAsync(Guid group, CancellationToken ct) => Run(() =>
        _groups.TryGetValue(group, out var g)
            ? new GroupDetail(g.Id, g.Name, g.Charter, NameOf(g.Founder), g.Open, g.OwnerRole,
                g.Members.Select(m => new GroupMember(m.Key, NameOf(m.Key), GroupRules.IsLocal(m.Key, out _),
                        m.Value.Select(r => r.Role).ToList(), m.Value.Any(r => r.Owner)))
                    .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList())
            : null);

    public Task<RenameResult> UpdateAsync(Guid group, string name, string charter, CancellationToken ct) => Run(() =>
    {
        if (!_groups.TryGetValue(group, out var g)) return RenameResult.NoGroup;
        if (_groups.Values.Any(o => o.Id != group && string.Equals(o.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return RenameResult.NameTaken;
        g.Name = name.Trim();
        g.Charter = charter;
        return RenameResult.Done;
    });

    public Task<AddResult> AddMemberAsync(Guid group, Guid principal, CancellationToken ct) => Run(() =>
    {
        if (!_groups.TryGetValue(group, out var g)) return AddResult.NoGroup;
        if (g.Members.ContainsKey(principal.ToString())) return AddResult.AlreadyMember;
        g.Members[principal.ToString()] = [("Everyone", false)];
        return AddResult.Added;
    });

    public Task<bool> RemoveMemberAsync(Guid group, string principalId, CancellationToken ct) => Run(() =>
        _groups.TryGetValue(group, out var g) && g.Members.Remove(principalId));

    public Task<bool> DeleteAsync(Guid group, CancellationToken ct) => Run(() => _groups.Remove(group));

    public Task<IReadOnlyList<string>> MemberIdsAsync(Guid group, CancellationToken ct) => Run<IReadOnlyList<string>>(() =>
        _groups.TryGetValue(group, out var g) ? g.Members.Keys.ToList() : []);

    public Task<IReadOnlyList<Guid>> ActiveResidentsAsync(CancellationToken ct) => Run<IReadOnlyList<Guid>>(() =>
        robust.All.Where(a => a.UserLevel >= 0).Select(a => a.PrincipalId).ToList());

    private string NameOf(string principalId)
    {
        if (!GroupRules.IsLocal(principalId, out var id)) return GroupRules.DisplayName(principalId, null, null);
        var a = robust.All.FirstOrDefault(x => x.PrincipalId == id);
        return GroupRules.DisplayName(principalId, a?.FirstName, a?.LastName);
    }

    private Task<T> Run<T>(Func<T> work)
    {
        lock (_gate)
        {
            if (_failAfter is { } n && _calls++ >= n) throw new GroupsUnavailableException("The database could not be read.");
            return Task.FromResult(work());
        }
    }
}
