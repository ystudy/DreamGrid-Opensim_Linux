using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>GridHostedService on a real Kestrel listener, and the launcher process when its web port is taken.</summary>
public sealed class GridHostedServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static WebApplication Host(FakeGridController grid, int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton<IGridController>(grid);
        builder.Services.AddHostedService<GridHostedService>();
        var app = builder.Build();
        app.MapGet("/", () => "up");
        return app;
    }

    private static string Dotnet() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private static async Task<bool> CanConnectAsync(int port)
    {
        using var c = new TcpClient();
        try { await c.ConnectAsync(IPAddress.Loopback, port); return true; }
        catch (SocketException) { return false; }
    }

    [Fact]
    public async Task Grid_starts_only_once_the_web_port_is_listening()
    {
        var port = FreePort();
        bool? listening = null;
        var grid = new FakeGridController { OnStart = async _ => listening = await CanConnectAsync(port) };
        await using var app = Host(grid, port);

        await app.StartAsync(Ct);
        await grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await app.StopAsync(Ct);

        Assert.True(listening);
    }

    [Fact]
    public async Task Host_start_does_not_wait_for_the_grid()
    {
        var port = FreePort();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grid = new FakeGridController { OnStart = _ => release.Task };
        await using var app = Host(grid, port);

        await app.StartAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(await CanConnectAsync(port));
        Assert.False(grid.Started.Task.IsCompleted);
        release.SetResult();
        await grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task Host_shutdown_detaches_and_never_stops_the_children()
    {
        var grid = new FakeGridController();
        await using var app = Host(grid, FreePort());

        await app.StartAsync(Ct);
        await grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await app.StopAsync(Ct);

        Assert.Equal(["StartAsync", "DetachAsync"], grid.Snapshot());
        Assert.DoesNotContain("StopAllAsync", grid.Snapshot());
    }

    [Fact]
    public async Task Host_shutdown_during_a_slow_grid_start_still_only_detaches()
    {
        var grid = new FakeGridController { OnStart = ct => Task.Delay(Timeout.Infinite, ct) };
        await using var app = Host(grid, FreePort());

        await app.StartAsync(Ct);
        await app.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Contains("DetachAsync", grid.Snapshot());
        Assert.DoesNotContain("StopAllAsync", grid.Snapshot());
    }

    [Fact]
    public async Task Launcher_exits_non_zero_when_the_web_port_is_taken()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-busy-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var holder = new TcpListener(IPAddress.Any, 0);
        holder.Start();
        try
        {
            var port = ((IPEndPoint)holder.LocalEndpoint).Port;
            var store = new SettingsStore(Path.Combine(data, "settings.json"));
            store.Save(store.LoadOrCreate() with { WebPort = port });

            var psi = new ProcessStartInfo(Dotnet())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DreamGrid.Launcher.dll"));
            foreach (var a in new[] { "--data", data, "--opensim", Path.Combine(root, "program") }) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_ENVIRONMENT"] = "Production";
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(Ct);
            var stderr = p.StandardError.ReadToEndAsync(Ct);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { p.Kill(true); throw; }
            }
            var output = await stdout + await stderr;

            Assert.NotEqual(0, p.ExitCode);
            Assert.Contains($"Port {port} (Web) is already in use by another program.", output);
        }
        finally
        {
            holder.Stop();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Launcher_exits_zero_on_SIGTERM_and_only_detaches()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "SIGTERM is the systemd stop signal; Linux only");

        var root = Path.Combine(Path.GetTempPath(), "dg-term-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        try
        {
            var store = new SettingsStore(Path.Combine(data, "settings.json"));
            store.Save(store.LoadOrCreate() with { WebPort = FreePort() });

            // No OpenSim program dir: the grid start fails (logged) and no child is ever started.
            var psi = new ProcessStartInfo(Dotnet()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DreamGrid.Launcher.dll"));
            foreach (var a in new[] { "--data", data, "--opensim", Path.Combine(root, "no-program") }) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_ENVIRONMENT"] = "Production";
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

            var output = new System.Text.StringBuilder();
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                string Text() { lock (output) return output.ToString(); }
                for (var i = 0; i < 300 && !Text().Contains("The grid did not start"); i++) await Task.Delay(100, Ct);
                Assert.Contains("Application started", Text());

                using (var kill = Process.Start("kill", ["-TERM", p.Id.ToString()])) await kill.WaitForExitAsync(Ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await p.WaitForExitAsync(timeout.Token);

                Assert.Equal(0, p.ExitCode);
                Assert.Contains("Application is shutting down", Text());
            }
            finally
            {
                if (!p.HasExited) p.Kill(true);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
