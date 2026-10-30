using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Setup;

/// <summary>
/// The read, show and confirm steps of joining a Grid Asset Server, shared by the setup join step and Advanced settings.
/// The first post reads the join information and shows it (Pages/Shared/_JoinFound.cshtml); the confirm post reads it
/// again, never trusting posted values, and saves it with <see cref="Apply"/>.
/// </summary>
public static class JoinForm
{
    /// <summary>
    /// The join information at the typed <paramref name="address"/>, or null with the owner's message: a mistyped
    /// address, or the <see cref="GridJoinException"/> the read gave.
    /// </summary>
    public static async Task<(JoinedGrid? Found, string? Error)> ReadAsync(IGridJoinClient client, string? address, CancellationToken ct)
    {
        if (JoinAddress.Parse(address, out var error) is not { } at) return (null, error);
        try
        {
            return (await client.ReadAsync(at.Host, at.Port, ct), null);
        }
        catch (GridJoinException e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>The address as the owner would type it: the host, with the port only when it isn't the usual one.</summary>
    public static string AddressOf(JoinedGrid joined) =>
        joined.PublicPort == JoinAddress.DefaultPort ? joined.Host : $"{joined.Host}:{joined.PublicPort}";

    /// <summary>A warning naming both versions when the Grid Asset Server runs another DreamGrid version; null when they match.</summary>
    public static string? VersionWarning(JoinedGrid found) =>
        found.Version == LauncherVersion.Current
            ? null
            : $"The Grid Asset Server runs DreamGrid {found.Version}, and this computer runs DreamGrid {LauncherVersion.Current}. "
              + "Both computers should run the same version. You can still join, but update the older one when you can.";

    /// <summary>The settings after joining <paramref name="read"/>: the join itself, and the grid's name as this computer's.</summary>
    public static LauncherSettings Apply(LauncherSettings s, JoinedGrid read) => s with { Join = read, GridName = read.GridName };
}

/// <summary>What Pages/Shared/_JoinFound.cshtml shows: the join information read, and the address to read again on confirm.</summary>
/// <param name="Found">The join information the first read gave.</param>
/// <param name="Address">The address, as the owner would type it, posted back with "Join this grid" or "Change the address".</param>
/// <param name="Repoint">True on Advanced settings, where a finished Region Server moves its regions to another grid.</param>
public sealed record JoinFoundModel(JoinedGrid Found, string Address, bool Repoint = false);
