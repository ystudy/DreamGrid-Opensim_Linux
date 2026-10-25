namespace DreamGrid.Launcher.Core.Processes;

/// <summary>
/// How a crashing child is restarted: exponential backoff from <see cref="InitialDelay"/> up to
/// <see cref="MaxDelay"/>; after <see cref="MaxFailures"/> crashes within <see cref="Window"/> the child is Failed.
/// A default (zero) TimeSpan selects the documented default: 10 min window, 2 s initial, 60 s max.
/// </summary>
public sealed record RestartPolicy(
    int MaxFailures = 5,
    TimeSpan Window = default,
    TimeSpan InitialDelay = default,
    TimeSpan MaxDelay = default)
{
    public TimeSpan EffectiveWindow => Window == default ? TimeSpan.FromMinutes(10) : Window;
    public TimeSpan EffectiveInitialDelay => InitialDelay == default ? TimeSpan.FromSeconds(2) : InitialDelay;
    public TimeSpan EffectiveMaxDelay => MaxDelay == default ? TimeSpan.FromSeconds(60) : MaxDelay;

    /// <summary>Delay before the restart that follows the <paramref name="failureCount"/>-th recent failure (1-based).</summary>
    public TimeSpan DelayFor(int failureCount)
    {
        var exponent = Math.Clamp(failureCount - 1, 0, 30);
        var ticks = EffectiveInitialDelay.Ticks * Math.Pow(2, exponent);
        return ticks >= EffectiveMaxDelay.Ticks ? EffectiveMaxDelay : TimeSpan.FromTicks((long)ticks);
    }
}
