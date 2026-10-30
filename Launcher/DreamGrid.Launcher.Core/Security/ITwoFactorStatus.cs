namespace DreamGrid.Launcher.Core.Security;

/// <summary>Whether a person has two-factor sign-in. <see cref="Unknown"/> means the store could not be read (damaged): callers must fail closed.</summary>
public enum TwoFactorState { Off, On, Unknown }

/// <summary>Read-only view of the two-factor store, for the sign-in checks.</summary>
public interface ITwoFactorStatus
{
    TwoFactorState StateOf(Guid principal);
}
