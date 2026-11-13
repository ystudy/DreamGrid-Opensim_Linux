using GridGate.Launcher.Core.Estates;
using GridGate.Launcher.Core.Hypergrid;

namespace GridGate.Launcher.Tests;

/// <summary>Robust's hypergrid links and map in memory, answering as <see cref="GridHyperlinks"/> does.</summary>
internal sealed class FakeGridHyperlinks : IGridHyperlinks
{
    private readonly Lock _gate = new();
    private readonly List<Hyperlink> _links = [];
    private readonly List<MapSpace> _regions = [];

    /// <summary>Set: every call throws <see cref="HyperlinksUnavailableException"/>.</summary>
    public bool Down { get; set; }

    public Guid Add(string name, string address, int x, int y)
    {
        var id = Guid.NewGuid();
        lock (_gate) _links.Add(new Hyperlink(id, name, address, x, y));
        return id;
    }

    /// <summary>A region of the grid itself on the map.</summary>
    public void AddRegion(int x, int y, int size = 1)
    {
        lock (_gate) _regions.Add(new MapSpace(x, y, size, size, false));
    }

    public IReadOnlyList<Hyperlink> All { get { lock (_gate) return _links.ToList(); } }

    public Task<IReadOnlyList<Hyperlink>> ListAsync(CancellationToken ct)
    {
        ThrowIfDown();
        lock (_gate) return Task.FromResult<IReadOnlyList<Hyperlink>>(_links.OrderBy(l => l.Name).ToList());
    }

    public Task<IReadOnlyList<MapSpace>> MapAsync(CancellationToken ct)
    {
        ThrowIfDown();
        lock (_gate) return Task.FromResult<IReadOnlyList<MapSpace>>(_regions.Concat(_links.Select(l => new MapSpace(l.X, l.Y, 1, 1, true))).ToList());
    }

    public Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        ThrowIfDown();
        lock (_gate) return Task.FromResult(_links.RemoveAll(l => l.Id == id) > 0);
    }

    private void ThrowIfDown()
    {
        if (Down) throw new HyperlinksUnavailableException("down");
    }
}

/// <summary>The link test without a network: answers <see cref="Answer"/> and records what it was asked.</summary>
internal sealed class FakeGridProbe : IGridProbe
{
    public Func<Uri, ProbeResult> Answer { get; set; } = u => new ProbeResult(true, "Other Grid", $"Other Grid answered at {u.GetLeftPart(UriPartial.Authority)}.");
    public List<Uri> Asked { get; } = [];

    public Task<ProbeResult> CheckAsync(Uri gatekeeper, CancellationToken ct)
    {
        lock (Asked) Asked.Add(gatekeeper);
        return Task.FromResult(Answer(gatekeeper));
    }
}

/// <summary>The regions' estate tables in memory, answering as <see cref="GridEstates"/> does, with names from the fake Robust.</summary>
internal sealed class FakeGridEstates(FakeRobustAccounts robust) : IGridEstates
{
    private sealed class Estate
    {
        public required int Id { get; init; }
        public required string Name { get; set; }
        public required Guid Owner { get; set; }
        public List<Guid> Managers { get; } = [];
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Estate> _estates = [];
    private readonly Dictionary<Guid, int> _map = [];
    private int _next = 1;

    public bool Down { get; set; }

    public int Add(string name, Guid owner, params Guid[] regions)
    {
        lock (_gate)
        {
            var id = _next++;
            _estates[id] = new Estate { Id = id, Name = name, Owner = owner };
            foreach (var r in regions) _map[r] = id;
            return id;
        }
    }

    /// <summary>What a region program's <c>estate create</c> or <c>estate link region</c> did to the tables.</summary>
    public void Link(Guid region, int estate)
    {
        lock (_gate) _map[region] = estate;
    }

    public bool Exists(int id) { lock (_gate) return _estates.ContainsKey(id); }
    public string NameOf(int id) { lock (_gate) return _estates[id].Name; }
    public Guid OwnerOf(int id) { lock (_gate) return _estates[id].Owner; }
    public IReadOnlyList<Guid> ManagersOf(int id) { lock (_gate) return _estates[id].Managers.ToList(); }
    public void Remove(int id) { lock (_gate) _estates.Remove(id); }

    public Task<IReadOnlyList<EstateSummary>> ListAsync(CancellationToken ct)
    {
        ThrowIfDown();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<EstateSummary>>(_estates.Values.OrderBy(e => e.Name)
                .Select(e => new EstateSummary(e.Id, e.Name, e.Owner, Name(e.Owner), _map.Values.Count(v => v == e.Id), e.Managers.Count)).ToList());
    }

    public Task<EstateDetail?> GetAsync(int id, CancellationToken ct)
    {
        ThrowIfDown();
        lock (_gate)
            return Task.FromResult(_estates.TryGetValue(id, out var e)
                ? new EstateDetail(e.Id, e.Name, e.Owner, Name(e.Owner), e.Managers.Select(m => new EstatePerson(m, Name(m))).ToList(),
                    _map.Where(p => p.Value == id).Select(p => p.Key).ToList())
                : null);
    }

    public Task<bool> NameTakenAsync(string name, int? except, CancellationToken ct)
    {
        ThrowIfDown();
        lock (_gate) return Task.FromResult(_estates.Values.Any(e => e.Id != except && string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    public Task<EstateChange> RenameAsync(int id, string name, CancellationToken ct) => Change(id, e =>
    {
        if (_estates.Values.Any(o => o.Id != id && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))) return EstateChange.NameTaken;
        e.Name = name;
        return EstateChange.Done;
    });

    public Task<EstateChange> SetOwnerAsync(int id, Guid owner, CancellationToken ct) => Change(id, e =>
    {
        e.Owner = owner;
        e.Managers.Remove(owner);
        return EstateChange.Done;
    });

    public Task<EstateChange> AddManagerAsync(int id, Guid manager, CancellationToken ct) => Change(id, e =>
    {
        if (e.Managers.Contains(manager)) return EstateChange.AlreadyManager;
        if (e.Managers.Count >= GridEstates.MaxManagers) return EstateChange.TooManyManagers;
        e.Managers.Add(manager);
        return EstateChange.Done;
    });

    public Task<EstateChange> RemoveManagerAsync(int id, Guid manager, CancellationToken ct) =>
        Change(id, e => e.Managers.Remove(manager) ? EstateChange.Done : EstateChange.NotAManager);

    public Task<IReadOnlyDictionary<Guid, int>> RegionEstatesAsync(CancellationToken ct)
    {
        ThrowIfDown();
        lock (_gate) return Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>(_map));
    }

    private Task<EstateChange> Change(int id, Func<Estate, EstateChange> work)
    {
        ThrowIfDown();
        lock (_gate) return Task.FromResult(_estates.TryGetValue(id, out var e) ? work(e) : EstateChange.NoEstate);
    }

    private string Name(Guid id) => robust.All.FirstOrDefault(a => a.PrincipalId == id) is { } a ? $"{a.FirstName} {a.LastName}" : id.ToString("D");

    private void ThrowIfDown()
    {
        if (Down) throw new EstatesUnavailableException("down");
    }
}
