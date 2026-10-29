using System.Net;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// On a first launch settings.json does not exist yet and LoadOrCreate generates fresh secrets. A status poll
/// racing the controller's first settings load must not produce a second secret set.
/// </summary>
public sealed class GridControllerFirstLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-first-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Status_poll_racing_the_first_load_yields_one_set_of_secrets()
    {
        for (var i = 0; i < 25; i++)
        {
            var paths = new LauncherPaths(Path.Combine(_root, "d" + i), Path.Combine(_root, "program"), null);
            var store = new SettingsStore(paths.SettingsFile);
            var console = new RecordingConsole();
            var grid = new GridController(paths, store, new MariaDbBinaries("mariadbd", "install-db", null),
                [new LogLevelModule()], new NoAccounts(), console, TimeProvider.System);

            using var go = new ManualResetEventSlim();
            var polls = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { go.Wait(); grid.GetStatus(); }, TestContext.Current.CancellationToken)).ToArray();
            // CreateOwnerAsync loads the settings (building the components from them), then talks to the console.
            var owner = Task.Run(async () =>
            {
                go.Wait();
                await grid.CreateOwnerAsync("Test", "Owner", "pw1", null, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
            go.Set();
            await Task.WhenAll(polls);
            await Assert.ThrowsAsync<ConsoleAuthException>(() => owner);

            var saved = store.LoadOrCreate();
            Assert.Equal(saved.Console.User, Assert.Single(console.Users.Distinct()));
        }
    }

    private sealed class NoAccounts : IRobustClient
    {
        public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct) => Task.FromResult<RobustAccount?>(null);
        public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MapRegion>>([]);
    }

    /// <summary>A console that records the USER each session start used, then rejects it.</summary>
    private sealed class RecordingConsole : IHttpClientFactory
    {
        public List<string> Users { get; } = new();

        public HttpClient CreateClient(string name) => new(new StubHandler(r =>
        {
            switch (r.Uri.AbsolutePath)
            {
                case "/StartSession/":
                    lock (Users) Users.Add(r.Form("USER"));
                    return (HttpStatusCode.Unauthorized, ""); // fail fast once USER is recorded
                case "/SessionCommand/":
                    return (HttpStatusCode.OK, "<ConsoleSession><Result>OK</Result></ConsoleSession>");
                default:
                    return (HttpStatusCode.OK, "<ConsoleSession></ConsoleSession>");
            }
        }));
    }
}
