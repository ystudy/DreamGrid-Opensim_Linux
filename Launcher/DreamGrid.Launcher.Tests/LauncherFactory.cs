using System.Collections.Concurrent;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// The real launcher host on a TestServer, over a temp data dir, with a fake grid and a fake Robust.
/// Every log entry is captured in <see cref="Logs"/>.
/// </summary>
internal sealed class LauncherFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-host-" + Guid.NewGuid().ToString("N"));

    public LauncherFactory(Func<LauncherSettings, LauncherSettings>? seed = null)
    {
        Paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        Store = new SettingsStore(Paths.SettingsFile);
        var s = Store.LoadOrCreate();
        Store.Save(seed is null ? s : seed(s));
        Grid.SettingsSource = Store.LoadOrCreate;
        Grid.SettingsSink = Store.Save;
    }

    public LauncherPaths Paths { get; }
    public SettingsStore Store { get; }
    public FakeGridController Grid { get; } = new();
    public FakeRobustAccounts Robust { get; } = new();
    public FakeOnlineUsers OnlineUsers { get; } = new();
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Logs { get; } = new();

    /// <summary>Runs before the app's own middleware (e.g. to fake the remote address).</summary>
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder>? Before { get; init; }

    /// <summary>Extra test-only registrations (e.g. a module), added after the fakes.</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DREAMGRID_DATA", Paths.Data);
        builder.UseSetting("DREAMGRID_OPENSIM", Paths.OpenSimProgram);
        builder.ConfigureLogging(l => l.AddProvider(new CapturingLoggerProvider(Logs)));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGridController>();
            services.AddSingleton<IGridController>(Grid);
            services.RemoveAll<IRobustClient>();
            services.AddSingleton<IRobustClient>(Robust);
            services.RemoveAll<IOnlineUsers>();
            services.AddSingleton<IOnlineUsers>(OnlineUsers);
            if (Before is { } before)
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new BeforeFilter(before));
            ExtraServices?.Invoke(services);
        });
    }

    public async Task<bool> WaitForLogAsync(Func<(LogLevel Level, string Category, string Message), bool> match)
    {
        for (var i = 0; i < 100; i++)
        {
            if (Logs.Any(match)) return true;
            await Task.Delay(50);
        }
        return false;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class BeforeFilter(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> before) : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app => { before(app); next(app); };
    }
}

internal sealed class CapturingLoggerProvider(ConcurrentQueue<(LogLevel Level, string Category, string Message)> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(sink, categoryName);

    public void Dispose() { }

    private sealed class Logger(ConcurrentQueue<(LogLevel, string, string)> sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue((logLevel, category, formatter(state, exception) + (exception is null ? "" : " | " + exception)));
    }
}
