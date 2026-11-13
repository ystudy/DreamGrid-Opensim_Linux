namespace GridGate.Launcher.Core.OpenSim;

/// <summary>The one rule for "Robust didn't answer". Callers that also read the staff list add <c>StaffDirectoryUnavailableException</c> themselves.</summary>
public static class RobustFailure
{
    /// <summary>True when <paramref name="e"/> means Robust could not be reached or its reply made no sense, and the caller was still waiting (<paramref name="ct"/> not cancelled).</summary>
    public static bool IsUnreachable(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or InvalidDataException
        && !ct.IsCancellationRequested;
}
