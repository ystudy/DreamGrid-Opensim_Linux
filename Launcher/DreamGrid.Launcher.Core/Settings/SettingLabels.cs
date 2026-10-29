namespace DreamGrid.Launcher.Core.Settings;

/// <summary>
/// The words the website shows for physics engines, map levels and priorities: one place, so the settings tabs and the
/// lines that describe a change always agree.
/// </summary>
public static class SettingLabels
{
    /// <summary>"ubODE", "BulletSim" or "BulletSim in its own thread"; an unknown engine as it is stored.</summary>
    public static string Physics(string engine) => engine == "BulletSimThreaded" ? "BulletSim in its own thread" : engine;

    public static string Map(MapQuality map) => map == MapQuality.None ? "No map" : map.ToString();

    public static string Priority(CpuPriority priority) => priority switch
    {
        CpuPriority.BelowNormal => "Below normal",
        CpuPriority.AboveNormal => "Above normal",
        CpuPriority.RealTime => "Real time",
        _ => priority.ToString(),
    };
}
