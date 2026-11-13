using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Email;

/// <summary>The grid owner's email address from the account service, for the test messages the email pages send; none when it can't be found.</summary>
public static class OwnerEmail
{
    public static async Task<string?> FindAsync(IRobustClient robust, LauncherSettings s, CancellationToken ct)
    {
        var owner = s.Setup.Owner;
        if (owner is null) return null;
        try
        {
            var account = await robust.FindAccountAsync(owner.FirstName, owner.LastName, ct);
            return string.IsNullOrWhiteSpace(account?.Email) ? null : account.Email;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}
