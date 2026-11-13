using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Core.Config;

/// <summary>Module seam (spec 6.9): modules add INI entries, targeted at "robust" or "region".</summary>
public interface IIniContributor
{
    IEnumerable<IniOverride> Contribute(LauncherSettings s);
}
