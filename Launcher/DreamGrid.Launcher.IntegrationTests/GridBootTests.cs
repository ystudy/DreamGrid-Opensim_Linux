using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>Boots the real MariaDB, Robust and one region through <see cref="GridController"/>.</summary>
[Collection(GridCollection.Name)]
public sealed class GridBootTests : IDisposable
{
    private readonly string _root = GridTestSupport.NewDataDir();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };

    public void Dispose()
    {
        KillEverythingUnder(_root);
        _http.Dispose();
        GridTestSupport.DeleteDataDir(_root);
    }

    private sealed record Rig(GridController Grid, LauncherPaths Paths, LauncherSettings Settings, RobustClient Robust);

    private Rig Make()
    {
        var paths = GridTestSupport.RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var settings = GridTestSupport.SeedSettings(store);
        var robust = GridTestSupport.NewRobustClient(_http, settings);
        return new Rig(GridTestSupport.NewController(paths, store, robust), paths, settings, robust);
    }

    [Fact]
    public async Task Fresh_grid_boots_owner_and_region()
    {
        var (grid, paths, s, robust) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            var status = grid.GetStatus();
            Assert.Equal(ProcessState.Running, status.Database.State);
            Assert.Equal(ProcessState.Running, status.Robust.State);
            Assert.Null(status.Region);
            Assert.Equal($"http://127.0.0.1:{s.RobustPublicPort}", status.LoginUri);

            var account = await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            Assert.Equal(200, account.UserLevel);
            Assert.True(await robust.AuthenticateAsync(account.PrincipalId, "pass-word1", ct));
            Assert.False(await robust.AuthenticateAsync(account.PrincipalId, "wrong", ct));

            // Task 11's /dreamgrid/regionready endpoint does not exist yet: stand in for it on the web port.
            using var alerts = new AlertListener(s.WebPort, grid.OnRegionAlert);
            var stages = new StageRecorder();
            await grid.CreateRegionAsync("Welcome", "island", ct, stages);
            Assert.True(grid.GetStatus().LoginsEnabled);
            Assert.Equal(["Creating your region", "Loading the region content", "Waiting for the region to accept logins"], stages.Stages);

            status = grid.GetStatus();
            Assert.Equal(ProcessState.Running, status.Region!.State);
            Assert.Equal($"http://127.0.0.1:{s.RobustPublicPort}", status.LoginUri);
            Assert.True(new SettingsStore(paths.SettingsFile).LoadOrCreate().Setup.RegionCreated);

            var dbPid = status.Database.Pid;
            Assert.NotNull(dbPid);
            await grid.StopAllAsync(ct);

            Assert.False(AnyProcessMatches(Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll")), "an OpenSim.dll process remains");
            Assert.False(AnyProcessMatches(Path.Combine(paths.RuntimeOpenSim, "Robust.dll")), "a Robust.dll process remains");
            Assert.False(ProcessImage.IsAlive(dbPid.Value), "MariaDB is still running");
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            throw new Exception(e.Message + Environment.NewLine + LogTail(paths), e);
        }
        finally
        {
            await grid.DetachAsync();
        }
    }

    [Fact]
    public async Task Port_conflict_on_Robust_public_port_is_reported_before_anything_starts()
    {
        var (grid, paths, s, _) = Make();
        var holder = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        holder.Start();
        try
        {
            var e = await Assert.ThrowsAsync<PortConflictException>(() => grid.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains(s.RobustPublicPort.ToString(), e.Message);
            Assert.Null(PidFile.Read(Path.Combine(paths.Pids, "mariadb.pid")));
            Assert.False(AnyProcessMatches(paths.MariaDbData), "MariaDB was started");
        }
        finally
        {
            holder.Stop();
            await grid.DetachAsync();
        }
    }
}

/// <summary>Records reported stages synchronously (Progress&lt;T&gt; would post them to the thread pool, out of order).</summary>
internal sealed class StageRecorder : IProgress<string>
{
    public List<string> Stages { get; } = new();

    public void Report(string value)
    {
        lock (Stages) Stages.Add(value);
    }
}
