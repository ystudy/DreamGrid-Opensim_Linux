using DreamGrid.Launcher.Core.Security;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// Accounts that must not be deleted: anyone an open takedown case names (worklist 2.5b). 2.5c (history for legal requests) will
/// add its own reasons here. The People page's delete asks this one place before removing anyone. When the takedown records
/// couldn't be read, everyone is held: refusing a delete is better than destroying evidence.
/// </summary>
public class AccountHolds
{
    public const string Unreadable = "The takedown records couldn't be read, so nobody can be deleted now.";

    private readonly TakedownStore? _store;

    public AccountHolds(TakedownStore store) => _store = store;

    /// <summary>For test fakes that decide for themselves: nobody is held.</summary>
    protected AccountHolds() { }

    /// <summary>Why <paramref name="principal"/> can't be deleted, or null when they can.</summary>
    public virtual string? Refusal(Guid principal)
    {
        if (_store is null) return null;
        if (_store.Unreadable) return Unreadable;
        return OpenCasesOf(principal) is [var first, ..] ? "This account is on hold: takedown " + first : null;
    }

    public bool IsHeld(Guid principal) => Refusal(principal) is not null;

    /// <summary>The numbers of the open cases that name <paramref name="principal"/>, newest first.</summary>
    public IReadOnlyList<string> OpenCasesOf(Guid principal) =>
        _store is null ? [] : [.. _store.Naming(principal).Where(c => c.Open).Select(c => c.Number)];
}
