using DreamGrid.Launcher.Core.Api;
using DreamGrid.Launcher.Core.Config;

namespace DreamGrid.Launcher.Core.Modules;

/// <summary>
/// A feature module (spec 6.9): INI entries through <see cref="IIniContributor"/>, settings stored under
/// <c>settings.Modules[Id]</c> (with <see cref="Defaults"/>), and optional port-8001 commands.
/// </summary>
public interface IFeatureModule : IIniContributor
{
    string Id { get; }
    string Title { get; }
    IReadOnlyDictionary<string, string> Defaults { get; }
    IEnumerable<LegacyCommand> Commands => [];
}
