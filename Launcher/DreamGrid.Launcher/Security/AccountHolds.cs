namespace DreamGrid.Launcher.Security;

/// <summary>
/// Accounts that must not be deleted. Nothing is held yet: worklist 2.5b (inventory takedown) and 2.5c (history for legal
/// requests) will hold an account here, and the People page's delete asks this one place before removing anyone.
/// </summary>
public class AccountHolds
{
    public virtual bool IsHeld(Guid principal) => false;
}
