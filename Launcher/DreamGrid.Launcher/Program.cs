using System.Net;
using DreamGrid.Launcher;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Api;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Connections;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();
builder.Host.UseWindowsService(o => o.ServiceName = "DreamGrid");

// The configuration carries the environment, so DREAMGRID_* can also come from the host settings (tests).
var config = builder.Configuration;
var env = new Dictionary<string, string?>
{
    ["DREAMGRID_DATA"] = config["DREAMGRID_DATA"],
    ["DREAMGRID_OPENSIM"] = config["DREAMGRID_OPENSIM"],
    ["DREAMGRID_MARIADB_BIN"] = config["DREAMGRID_MARIADB_BIN"],
    ["ProgramData"] = Environment.GetEnvironmentVariable("ProgramData"),
};
LauncherPaths paths;
SettingsStore store;
LauncherSettings settings;
try
{
    paths = LauncherPaths.Resolve(args, env, OperatingSystem.IsWindows());
    store = new SettingsStore(paths.SettingsFile);
    settings = store.LoadOrCreate();
}
catch (Exception e) when (e is ArgumentException or SettingsCorruptException or IOException or UnauthorizedAccessException)
{
    // Nothing can be served without the data directory and settings: one plain line, then a non-zero exit
    // (2; the taken web port is 1).
    using (var early = LoggerFactory.Create(b => b.AddConsole()))
        early.CreateLogger("DreamGrid.Launcher").LogCritical("The launcher cannot start: {Reason}", e.Message);
    return 2;
}

builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, settings.WebPort));
// The request log line includes the query string, which carries ?password= (MachineId or an avatar's).
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
// Health polls of children that are still booting would log a stack trace every few seconds.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Logging.AddProvider(new RollingFileLoggerProvider(Path.Combine(paths.Logs, "launcher.log")));

builder.Services.AddHttpClient();
builder.Services.AddRazorPages();
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(store);
// Read per request from the controller's cache (never the file), so a change the controller saved is seen at once.
builder.Services.AddSingleton<Func<LauncherSettings>>(sp => sp.GetRequiredService<IGridController>().CurrentSettings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
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
builder.Services.AddSingleton<IFeatureModule, LogLevelModule>();
builder.Services.AddSingleton<IRobustClient>(sp => new RobustClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("DreamGrid.Robust"),
    new Uri($"http://127.0.0.1:{settings.RobustPrivatePort}"),
    new Uri($"http://127.0.0.1:{settings.RobustPublicPort}")));
builder.Services.AddSingleton<GridController>();
builder.Services.AddSingleton<IGridController>(sp => sp.GetRequiredService<GridController>());
builder.Services.AddSingleton<LegacyCommandApi>();
builder.Services.AddHostedService<GridHostedService>();

var app = builder.Build();
app.UseStaticFiles();
LegacyEndpoints.Map(app);
app.MapRazorPages();

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Plan 3's installers print and open this link; it is shown until the owner avatar exists.
    var s = app.Services.GetRequiredService<IGridController>().CurrentSettings();
    if (s.SetupToken is { } token && !s.Setup.OwnerCreated)
        app.Logger.LogInformation("Setup link: http://{Host}:{Port}/setup?token={Token}",
            s.Hostname ?? HostAddress.Detect(), s.WebPort, token);
});

try
{
    await app.StartAsync();
}
catch (Exception e) when (IsAddressInUse(e))
{
    // The website cannot show this one: nothing is listening.
    app.Logger.LogCritical("Port {Port} (Web) is already in use by another program.", settings.WebPort);
    await app.DisposeAsync();
    return 1;
}

await app.WaitForShutdownAsync();
await app.DisposeAsync();
return 0;

static bool IsAddressInUse(Exception? e)
{
    for (; e is not null; e = e.InnerException)
        if (e is AddressInUseException) return true;
    return false;
}

public partial class Program { }
