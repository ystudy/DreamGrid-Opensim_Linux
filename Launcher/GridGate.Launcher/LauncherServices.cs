using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Api;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Modules;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher;

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
        services.AddSingleton(PlatformSupport.Current);
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
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("GridGate.Robust"),
            // Resolved per call: the controller itself depends on this client.
            () => sp.GetRequiredService<IGridController>().CurrentSettings()));
        // Assets go up to 64 MB, so this client waits longer than the account lookups do.
        services.AddHttpClient("GridGate.RobustAssets", c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddSingleton<IRobustAssets>(sp => new RobustAssets(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("GridGate.RobustAssets"),
            () => sp.GetRequiredService<IGridController>().CurrentSettings()));
        services.AddHttpClient("GridGate.Join", c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IGridJoinClient>(sp => new GridJoinClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("GridGate.Join")));
        services.AddHttpClient("GridGate.OtherGrid", c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IOtherGridClient>(sp => new OtherGridClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("GridGate.OtherGrid")));
        services.AddHttpClient("GridGate.PublicAddress", c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<IPublicAddressFinder>(sp => new PublicAddressFinder(sp.GetRequiredService<IHttpClientFactory>().CreateClient("GridGate.PublicAddress")));
        services.AddSingleton<GridController>();
        services.AddSingleton<IGridController>(sp => sp.GetRequiredService<GridController>());
        return services;
    }
}
