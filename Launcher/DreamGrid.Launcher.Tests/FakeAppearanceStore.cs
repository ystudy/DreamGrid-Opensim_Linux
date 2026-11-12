using DreamGrid.Launcher.Core.Database;

namespace DreamGrid.Launcher.Tests;

/// <summary>Stored appearances for tests: what each account wears, by a function so a test can make a copy "arrive" late.</summary>
public sealed class FakeAppearanceStore : IAppearanceStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Func<IReadOnlyList<WornItem>?>> _looks = [];
    private readonly List<Guid> _asked = [];

    public bool Down { get; set; }

    public void Set(Guid principal, IReadOnlyList<WornItem>? worn) => Set(principal, () => worn);

    public void Set(Guid principal, Func<IReadOnlyList<WornItem>?> worn)
    {
        lock (_gate) _looks[principal] = worn;
    }

    /// <summary>Every account asked about, in order.</summary>
    public IReadOnlyList<Guid> Asked
    {
        get { lock (_gate) return [.. _asked]; }
    }

    public Task<IReadOnlyList<WornItem>?> WornAsync(Guid principal, CancellationToken ct)
    {
        Func<IReadOnlyList<WornItem>?>? look;
        lock (_gate)
        {
            _asked.Add(principal);
            if (Down) return Task.FromException<IReadOnlyList<WornItem>?>(new StaffDirectoryUnavailableException("down", new IOException()));
            _looks.TryGetValue(principal, out look);
        }
        return Task.FromResult(look?.Invoke());
    }
}
