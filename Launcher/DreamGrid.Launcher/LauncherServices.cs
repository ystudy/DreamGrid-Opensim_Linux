using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Api;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher;

public static class LauncherServices
{
    /// <summary>
    /// Everything the <see cref="GridController"/> needs, shared by the host and the stop-grid verb so both compose
    /// the controller the same way.
    /// </summary>
    public static IServiceCollection AddGrid(this IServiceCollection services, LauncherPaths paths, SettingsStore store)
    {
        services.AddHttpClient();
        services.AddSingleton(paths);
        services.AddSingleton(store);
        // Read per request from the controller's cache (never the file), so a change the controller saved is seen at once.
        services.AddSingleton<Func<LauncherSettings>>(sp => sp.GetRequiredService<IGridController>().CurrentSettings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            try
            {
                return MariaDbBinaries.Locate(paths.MariaDbBin, OperatingSystem.IsWindows());
            }
            catch (MariaDbNotFoundException e)
            {
                // Not fatal: the host still serves the web port and the grid status; the grid start reports this message.
                return MariaDbBinaries.NotFound(e.Message);
            }
        });
        // One instance per module, registered under each seam it implements (spec 6.9).
        services.AddSingleton<LogLevelModule>();
        services.AddSingleton<IFeatureModule>(sp => sp.GetRequiredService<LogLevelModule>());
        services.AddSingleton<ISettingsPage>(sp => sp.GetRequiredService<LogLevelModule>());
        services.AddSingleton<IRobustClient>(sp => new RobustClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DreamGrid.Robust"),
            // Resolved per call: the controller itself depends on this client.
            () => sp.GetRequiredService<IGridController>().CurrentSettings()));
        services.AddHttpClient("DreamGrid.Join", c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IGridJoinClient>(sp => new GridJoinClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("DreamGrid.Join")));
        services.AddHttpClient("DreamGrid.OtherGrid", c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IOtherGridClient>(sp => new OtherGridClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("DreamGrid.OtherGrid")));
        services.AddHttpClient("DreamGrid.PublicAddress", c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<IPublicAddressFinder>(sp => new PublicAddressFinder(sp.GetRequiredService<IHttpClientFactory>().CreateClient("DreamGrid.PublicAddress")));
        services.AddSingleton<GridController>();
        services.AddSingleton<IGridController>(sp => sp.GetRequiredService<GridController>());
        return services;
    }
}
