using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Two-factor sign-in for residents: its own <see cref="TwoFactorStore"/> (<c>&lt;data&gt;/guests/twofactor.json</c>), apart from
/// the staff one, so the guest door asks for a code only from someone who set it up as a resident and no staff sign-in changes.
/// It sleeps until the owner's residents' rule asks for a code: only then can a resident set it up. Someone who already has it on
/// keeps it, and its page, whatever the rule says later.
/// </summary>
public sealed class ResidentTwoFactor(TwoFactorStore store, string file)
{
    public TwoFactorStore Store { get; } = store;

    /// <summary>The residents' file, for the owner to set aside when it is damaged (Security page).</summary>
    public string File { get; } = file;

    /// <summary>Whether the resident's two-factor page exists for someone whose state is <paramref name="state"/>.</summary>
    public static bool PageShown(LauncherSettings s, TwoFactorState state) =>
        state == TwoFactorState.On || Recovery.Rules(s).Residents == RecoveryRule.EmailLinkAndCode;
}
