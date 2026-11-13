using GridGate.Launcher.Core;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Security;

namespace GridGate.Launcher.Estates;

/// <summary>
/// Finds a resident of this grid by the name staff typed, with the strict lookup the Staff and Groups pages use: an unusable
/// reply is the service failing, never "no such avatar".
/// </summary>
public static class AvatarLookup
{
    public static async Task<(RobustAccount? Account, string? Problem)> FindAsync(IRobustClient robust, string? typed, ILogger log, CancellationToken ct)
    {
        if (AvatarName.Parse(typed) is not { IsComplete: true } who) return (null, "Type the avatar's first and last name.");
        try
        {
            return await robust.FindAccountAsync(who.First, who.Last, ct) is { } account
                ? (account, null)
                : (null, $"No avatar called {LogText.Clean(who.Key, 64)}.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
            or InvalidDataException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Estates: the account service did not answer: {Message}", LogText.Clean(e.Message, 300));
            return (null, StaffActions.RobustDown);
        }
    }
}
