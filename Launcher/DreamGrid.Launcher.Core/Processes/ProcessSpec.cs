using System.Diagnostics;

namespace DreamGrid.Launcher.Core.Processes;

/// <summary>
/// Everything needed to run and supervise one child. <paramref name="ExpectedImage"/> is a substring the main module
/// path or command line of an adoptable process must contain (for example <c>OpenSim.dll</c>); null disables adoption.
/// </summary>
public sealed record ProcessSpec(
    string Name,
    Func<ProcessStartInfo> StartInfo,
    string PidFile,
    Func<CancellationToken, Task<bool>> IsHealthy,
    Func<CancellationToken, Task> GracefulStop,
    TimeSpan StartTimeout,
    TimeSpan StopTimeout,
    string? ExpectedImage);
