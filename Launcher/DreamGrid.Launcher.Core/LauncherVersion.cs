using System.Reflection;

namespace DreamGrid.Launcher.Core;

/// <summary>The launcher's version as built; the site footer, <c>--version</c> and the grid information show the same text.</summary>
public static class LauncherVersion
{
    /// <summary>The entry assembly's InformationalVersion (e.g. 0.0.0-dev+abc1234), else "dev".</summary>
    public static string Current { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } v ? v : "dev";
}
