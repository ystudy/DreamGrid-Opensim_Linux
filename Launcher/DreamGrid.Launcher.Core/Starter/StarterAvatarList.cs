using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Starter;

/// <summary>
/// One starter avatar (spec 2026-11-11-starter-avatars-design §6.6): its display name, its model account, its place in the list,
/// whether the owner hid it, its snapshot's file name, its last check and the warning a failed copy left.
/// </summary>
public sealed record StarterAvatar(Guid Id, string Name, Guid Model, string ModelFirst, string ModelLast, int Order, bool Hidden,
    string? Snapshot, DateTimeOffset? CheckedAt, bool CheckPassed, string? Warning)
{
    /// <summary>Shown at sign-up: not hidden, has a snapshot, passed its check and has no warning since.</summary>
    public bool IsReady => !Hidden && Snapshot is not null && CheckPassed && Warning is null;

    public string ModelName => $"{ModelFirst} {ModelLast}";
}

/// <summary>Which starter avatar a resident picked at sign-up (§5.3), shown on their People page.</summary>
public sealed record StarterPick(Guid Principal, Guid Avatar, string Name, DateTimeOffset At);

/// <summary>What <see cref="StarterAvatarList"/> keeps on disk.</summary>
public sealed record StarterState(List<StarterAvatar> Avatars, Guid? DefaultId, List<StarterPick> Picks);

/// <summary>
/// The owner's starter avatars, their order and default, and residents' picks, in <c>&lt;data&gt;/starter-avatars/avatars.json</c>
/// (<see cref="JsonStateFile{T}"/>: a missing or damaged file is an empty list). Safe from any thread.
/// </summary>
public sealed class StarterAvatarList
{
    private readonly JsonStateFile<StarterState> _file;
    private readonly Lock _gate = new();
    private StarterState _state;

    public StarterAvatarList(string file, TimeProvider time, ILogger? log = null)
    {
        _ = time;
        _file = new JsonStateFile<StarterState>(file, () => new([], null, []), log);
        _state = _file.Load();
    }

    /// <summary>Every starter avatar, in the owner's order.</summary>
    public IReadOnlyList<StarterAvatar> All()
    {
        lock (_gate) return [.. _state.Avatars.OrderBy(a => a.Order)];
    }

    /// <summary>The ones sign-up shows, in order.</summary>
    public IReadOnlyList<StarterAvatar> Ready() => [.. All().Where(a => a.IsReady)];

    /// <summary>The avatar the owner marked as the default, ready or not.</summary>
    public Guid? DefaultId
    {
        get { lock (_gate) return _state.DefaultId; }
    }

    /// <summary>The owner's default when it is ready, else the first ready avatar, else null.</summary>
    public StarterAvatar? Default()
    {
        var ready = Ready();
        var chosen = DefaultId;
        return ready.FirstOrDefault(a => a.Id == chosen) ?? ready.FirstOrDefault();
    }

    /// <summary>What a sign-up gets for <paramref name="picked"/>: it when ready, else <see cref="Default"/>; null means the plain avatar.</summary>
    public StarterAvatar? Resolve(Guid? picked) =>
        picked is { } id && Ready().FirstOrDefault(a => a.Id == id) is { } a ? a : Default();

    /// <summary>Adds a new, not yet ready avatar at the end of the list.</summary>
    public StarterAvatar Add(string name, Guid model, string first, string last)
    {
        lock (_gate)
        {
            var order = _state.Avatars.Count == 0 ? 0 : _state.Avatars.Max(a => a.Order) + 1;
            var a = new StarterAvatar(Guid.NewGuid(), name.Trim(), model, first, last, order, false, null, null, false, null);
            Swap(_state with { Avatars = [.. _state.Avatars, a] });
            return a;
        }
    }

    /// <summary>Changes one avatar (its id and model stay); false when it isn't there or couldn't be saved.</summary>
    public bool Update(Guid id, Func<StarterAvatar, StarterAvatar> change)
    {
        lock (_gate)
        {
            if (_state.Avatars.FirstOrDefault(a => a.Id == id) is not { } old) return false;
            var next = change(old) with { Id = old.Id, Model = old.Model };
            return Swap(_state with { Avatars = [.. _state.Avatars.Select(a => a.Id == id ? next : a)] });
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            if (_state.Avatars.All(a => a.Id != id)) return false;
            return Swap(_state with
            {
                Avatars = [.. _state.Avatars.Where(a => a.Id != id)],
                DefaultId = _state.DefaultId == id ? null : _state.DefaultId,
            });
        }
    }

    /// <summary>Swaps the avatar with its neighbour <paramref name="delta"/> places away (−1 up, +1 down); false at either end.</summary>
    public bool Move(Guid id, int delta)
    {
        lock (_gate)
        {
            var ordered = _state.Avatars.OrderBy(a => a.Order).ToList();
            var i = ordered.FindIndex(a => a.Id == id);
            var j = i + Math.Sign(delta);
            if (i < 0 || delta == 0 || j < 0 || j >= ordered.Count) return false;
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
            return Swap(_state with { Avatars = [.. ordered.Select((a, n) => a with { Order = n })] });
        }
    }

    public bool SetDefault(Guid id)
    {
        lock (_gate)
        {
            if (_state.Avatars.All(a => a.Id != id)) return false;
            return Swap(_state with { DefaultId = id });
        }
    }

    /// <summary>Whether <paramref name="principal"/> is a starter avatar's model account.</summary>
    public bool IsModel(Guid principal)
    {
        lock (_gate) return _state.Avatars.Any(a => a.Model == principal);
    }

    /// <summary>Keeps a resident's pick (one per resident; a later one replaces it).</summary>
    public void Picked(StarterPick pick)
    {
        lock (_gate) Swap(_state with { Picks = [.. _state.Picks.Where(p => p.Principal != pick.Principal), pick] });
    }

    public StarterPick? PickOf(Guid principal)
    {
        lock (_gate) return _state.Picks.FirstOrDefault(p => p.Principal == principal);
    }

    private bool Swap(StarterState next)
    {
        if (!_file.Save(next)) return false;
        _state = next;
        return true;
    }
}

/// <summary>Model account names, <c>Starter&lt;NN&gt; Avatar</c> (§6.2): folder names like "Professional Lady #1" aren't avatar names.</summary>
public static partial class StarterNames
{
    public const string LastName = "Avatar";

    /// <summary>Whether the name is in the models' pattern, whatever its case; such a name is never given to a resident.</summary>
    public static bool IsModelName(string first, string last) =>
        string.Equals(last?.Trim(), LastName, StringComparison.OrdinalIgnoreCase) && FirstPattern().IsMatch(first?.Trim() ?? "");

    /// <summary>The lowest free <c>StarterNN</c>, from 01, given the first names already used.</summary>
    public static (string First, string Last) Next(IEnumerable<string> takenFirstNames)
    {
        var taken = takenFirstNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; n <= 99; n++)
        {
            var first = $"Starter{n:00}";
            if (!taken.Contains(first)) return (first, LastName);
        }
        throw new InvalidOperationException("All 99 starter avatar names are in use.");
    }

    [GeneratedRegex(@"^Starter\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex FirstPattern();
}
