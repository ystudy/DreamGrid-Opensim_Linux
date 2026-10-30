using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Ini;
using DreamGrid.Launcher.Core.Settings;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>
/// The website end to end: a newcomer goes from the setup link to the dashboard through plain HTTP, against the real
/// launcher, MariaDB, Robust and region. Part of <see cref="LauncherEndToEndTests"/> so it shares its launcher process
/// handling and cleanup (and runs one after the other with the other launcher tests).
/// </summary>
public sealed partial class LauncherEndToEndTests
{
    private const string Password = "pass-word1";

    [Fact]
    public async Task Newcomer_path_from_setup_link_to_dashboard()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            var launcher = StartLauncher(paths);
            await WaitForAsync(launcher, async () => await GetAsync(s, "?type=Booted") == "ACK", "GET /?type=Booted == ACK");
            using var web = new WebSession(s.WebPort);

            // 1. The setup link, as the installer shows it.
            string link = "";
            await WaitForAsync(launcher, () => Task.FromResult((link = SetupLink(paths)) != ""), "the setup link in launcher.log");
            Assert.StartsWith($"http://127.0.0.1:{s.WebPort}/setup?token=", link);

            // 2. The link sets the setup cookie and lands on the plan cards.
            var page = await web.FollowAsync(link);
            Assert.Equal("/setup/plan", page.Path);

            // 3. The Local plan from its own page, then the network step.
            page = await web.FollowAsync("/setup/plan/local");
            page = await web.SubmitAsync(page, new());
            Assert.Equal("/setup/network", page.Path);
            page = await web.SubmitAsync(page, new());
            Assert.Equal("/setup/world", page.Path);

            // The world's name.
            page = await web.SubmitAsync(page, new() { ["GridName"] = "Avalon" });
            Assert.Equal("/setup/avatar", page.Path);

            // 4. Step 2: the owner avatar (starts the database and Robust when they are not running yet).
            page = await web.SubmitAsync(page, new()
            {
                ["FirstName"] = "Test", ["LastName"] = "Owner", ["Password"] = Password, ["Confirm"] = Password,
            });
            Assert.Equal("/setup/region", page.Path);

            // 5. Step 3: the region, then its progress until it is ready.
            page = await web.SubmitAsync(page, new() { ["RegionName"] = "Welcome", ["Content"] = "island" });
            Assert.Equal("/setup/region?working=1", page.PathAndQuery);
            await WaitForAsync(launcher, () => RegionReadyAsync(web), "/api/setup/progress to reach \"Ready\"", () => "last progress: " + _lastProgress);
            page = await web.FollowAsync("/setup");
            Assert.Equal("/setup/connect", page.Path);

            // 6. Step 4: the login URI, then "I'm in!".
            Assert.Contains($"http://127.0.0.1:{s.RobustPublicPort}", page.Html);
            page = await web.SubmitAsync(page, new());
            Assert.Equal("/", page.Path);

            // 7. The dashboard.
            Assert.Contains("Your world is running", page.Html);
            Assert.Contains("Avalon", page.Html);

            // 8. Sign out; the dashboard then asks to sign in, and signing in leads back to it.
            page = await web.SubmitAsync(page, new(), "form.signout");
            Assert.Equal("/login", page.Path);
            page = await web.FollowAsync("/");
            Assert.Equal("/login", page.Path);
            page = await web.SubmitAsync(page, new() { ["Name"] = "Test Owner", ["Password"] = Password });
            Assert.Equal("/", page.Path);
            Assert.Contains("Your world is running", page.Html);

            // 9. The setup link is spent.
            page = await web.FollowAsync(link);
            Assert.Equal("/setup-done", page.Path);
        });
    }

    [Fact]
    public async Task Grid_asset_lan_setup_with_hypergrid_off_boots_a_region()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            var launcher = StartLauncher(paths);
            await WaitForAsync(launcher, async () => await GetAsync(s, "?type=Booted") == "ACK", "GET /?type=Booted == ACK");
            using var web = new WebSession(s.WebPort);

            string link = "";
            await WaitForAsync(launcher, () => Task.FromResult((link = SetupLink(paths)) != ""), "the setup link in launcher.log");
            var page = await web.FollowAsync(link);
            Assert.Equal("/setup/plan", page.Path);

            // The Grid Asset plan, with a LAN address (a made-up public address would become the services' BaseURL,
            // and the region could not reach Robust on a test machine) and the hypergrid switch left off.
            page = await web.FollowAsync("/setup/plan/grid-asset");
            page = await web.SubmitAsync(page, new());
            Assert.Equal("/setup/network", page.Path);
            page = await web.SubmitAsync(page, new() { ["AddressKind"] = "lan" });
            Assert.Equal("/setup/world", page.Path);
            page = await web.SubmitAsync(page, new() { ["GridName"] = "Avalon" });
            Assert.Equal("/setup/avatar", page.Path);
            page = await web.SubmitAsync(page, new()
            {
                ["FirstName"] = "Test", ["LastName"] = "Owner", ["Password"] = Password, ["Confirm"] = Password,
            });
            Assert.Equal("/setup/region", page.Path);

            // Create the region (not Skip, whose form comes first on this plan) and wait until it is ready (logins enabled).
            page = await web.SubmitAsync(page, new() { ["RegionName"] = "Welcome", ["Content"] = "island" }, "main > form");
            Assert.Equal("/setup/region?working=1", page.PathAndQuery);
            await WaitForAsync(launcher, () => RegionReadyAsync(web), "/api/setup/progress to reach \"Ready\"", () => "last progress: " + _lastProgress);

            var settings = store.LoadOrCreate();
            Assert.False(settings.Hypergrid);
            var region = RegionModel.FindByName(settings, "Welcome")!;
            var ini = IniDocument.Load(Path.Combine(paths.RegionDir(region.Instance), "OpenSim.ini"));
            Assert.Equal("config-include/Grid.ini", ini.Get("Architecture", "Include-Architecture"));
        });
    }

    [Fact]
    public async Task Region_server_joins_a_grid_asset_server_and_boots_a_region()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            // 1. Launcher A: a Grid Asset Server on the network, hypergrid off, with a region "Hub" (at 1000,1000).
            using var a = await OpenSetupAsync(paths, s, "A");
            var page = await a.Web.FollowAsync("/setup/plan/grid-asset");
            page = await a.Web.SubmitAsync(page, new());
            Assert.Equal("/setup/network", page.Path);
            page = await a.Web.SubmitAsync(page, new() { ["AddressKind"] = "lan" });
            Assert.Equal("/setup/world", page.Path);
            page = await a.Web.SubmitAsync(page, new() { ["GridName"] = "Avalon" });
            Assert.Equal("/setup/avatar", page.Path);
            await FinishSetupFromAvatarAsync(a, "A", page, OwnerWithPassword, "Hub", "main > form");

            // 2. Launcher B: its own data folder and free ports (picked now, so none is one A already uses).
            var pathsB = RequirePaths(_secondRoot);
            var storeB = new SettingsStore(pathsB.SettingsFile);
            var sB = SeedSettings(storeB);
            await RunWithCleanupAsync(pathsB, storeB, async () =>
            {
                using var b = await OpenSetupAsync(pathsB, sB, "B");
                var pageB = await b.Web.FollowAsync("/setup/plan/region-server");
                pageB = await b.Web.SubmitAsync(pageB, new());
                Assert.Equal("/setup/join", pageB.Path);

                // Join: the first post shows what was found; "Join this grid" saves it.
                pageB = await b.Web.SubmitAsync(pageB, new() { ["Address"] = $"127.0.0.1:{s.RobustPublicPort}" }, redirects: false);
                Assert.Contains("Avalon", pageB.Html);
                pageB = await b.Web.SubmitAsync(pageB, new(), ".join-found form", button: "button[formaction*='handler=Confirm']");
                Assert.Equal("/setup/network", pageB.Path);

                // On my network, with B's own free first region port (the suggestion follows A's region ports).
                pageB = await b.Web.SubmitAsync(pageB, new()
                {
                    ["AddressKind"] = "lan", ["FirstRegionPort"] = sB.RegionPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                Assert.Equal("/setup/avatar", pageB.Path);
                await FinishSetupFromAvatarAsync(b, "B", pageB, OwnerByName, "Isle");

                // 3. B joined Avalon, placed Isle beside Hub, runs no grid service, and A's map has both regions.
                var settingsB = storeB.LoadOrCreate();
                Assert.Equal("Avalon", settingsB.Join?.GridName);
                var isle = RegionModel.FindByName(settingsB, "Isle")!;
                Assert.Equal((1001, 1000), (isle.X, isle.Y));
                Assert.False(File.Exists(Path.Combine(pathsB.Pids, "robust.pid")), "B wrote a robust.pid; a Region Server runs no grid service");
                var map = await NewRobustClient(_http, store.LoadOrCreate()).GetMapRegionsAsync(Ct);
                Assert.Contains("Hub", map.Select(r => r.Name));
                Assert.Contains("Isle", map.Select(r => r.Name));

                await WaitForLoginsEnabledAsync(b.Launcher, sB, "B");
                // Signing in to B's website as the owner, with the password kept on A.
                await AssertWebsiteSignInAsync(sB);

                // 4. Stop A's world; then stop B's and start it again: B can't start without the Grid Asset Server, and
                // keeps its join.
                var joined = storeB.LoadOrCreate().Join;
                await AssertStartFailsWhileGridDownAsync(a, b, "isn't answering");
                Assert.Equal(joined, storeB.LoadOrCreate().Join);
            });
        });
    }

    [Fact]
    public async Task Connected_server_joins_another_grid_and_boots_a_region()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            // 1. Launcher A, a Local world "Elsewhere": the "other grid", with a region "Hub" (at 1000,1000).
            using var a = await OpenSetupAsync(paths, s, "A");
            var page = await a.Web.FollowAsync("/setup/plan/local");
            page = await a.Web.SubmitAsync(page, new());
            Assert.Equal("/setup/network", page.Path);
            page = await a.Web.SubmitAsync(page, new());
            Assert.Equal("/setup/world", page.Path);
            page = await a.Web.SubmitAsync(page, new() { ["GridName"] = "Elsewhere" });
            Assert.Equal("/setup/avatar", page.Path);
            await FinishSetupFromAvatarAsync(a, "A", page, OwnerWithPassword, "Hub");

            // 2. Launcher B: its own data folder and free ports (picked now, so none is one A already uses).
            var pathsB = RequirePaths(_secondRoot);
            var storeB = new SettingsStore(pathsB.SettingsFile);
            var sB = SeedSettings(storeB);
            await RunWithCleanupAsync(pathsB, storeB, async () =>
            {
                using var b = await OpenSetupAsync(pathsB, sB, "B");
                var pageB = await b.Web.FollowAsync("/setup/plan/connected-server");
                pageB = await b.Web.SubmitAsync(pageB, new());
                Assert.Equal("/setup/other-grid", pageB.Path);

                // Another grid, by its two addresses: the first post shows what was found; "Connect to this grid" saves it.
                pageB = await b.Web.SubmitAsync(pageB, new()
                {
                    ["Grid"] = "other",
                    ["LoginAddress"] = $"127.0.0.1:{s.RobustPublicPort}",
                    ["ServicesAddress"] = $"127.0.0.1:{s.RobustPrivatePort}",
                }, redirects: false);
                Assert.Contains("Elsewhere", pageB.Html);
                pageB = await b.Web.SubmitAsync(pageB, new(), ".join-found form", button: "button[formaction*='handler=Confirm']");
                Assert.Equal("/setup/network", pageB.Path);

                // "On my network", not a public address: both launchers run on this one machine. With B's own free first region port.
                pageB = await b.Web.SubmitAsync(pageB, new()
                {
                    ["AddressKind"] = "lan", ["FirstRegionPort"] = sB.RegionPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                Assert.Equal("/setup/avatar", pageB.Path);
                await FinishSetupFromAvatarAsync(b, "B", pageB, OwnerByName, "Isle");

                // 3. B is connected to Elsewhere by address (no preset), placed Isle beside Hub, runs no grid service, and
                // A's map has both regions.
                var settingsB = storeB.LoadOrCreate();
                Assert.Equal("Elsewhere", settingsB.Connected?.GridName);
                Assert.Null(settingsB.Connected?.Preset);
                var isle = RegionModel.FindByName(settingsB, "Isle")!;
                Assert.Equal((1001, 1000), (isle.X, isle.Y));
                Assert.False(File.Exists(Path.Combine(pathsB.Pids, "robust.pid")), "B wrote a robust.pid; a Connected Server runs no grid service");
                var map = await NewRobustClient(_http, store.LoadOrCreate()).GetMapRegionsAsync(Ct);
                Assert.Contains("Hub", map.Select(r => r.Name));
                Assert.Contains("Isle", map.Select(r => r.Name));

                await WaitForLoginsEnabledAsync(b.Launcher, sB, "B");
                // Signing in to B's website as the owner: Elsewhere checks the password.
                await AssertWebsiteSignInAsync(sB);

                // 4. Stop A's world; then stop B's and start it again: B can't start without Elsewhere answering, and keeps
                // its connection.
                var connected = storeB.LoadOrCreate().Connected;
                await AssertStartFailsWhileGridDownAsync(a, b, "Elsewhere isn't answering at");
                Assert.Equal(connected, storeB.LoadOrCreate().Connected);
            });
        });
    }

    /// <summary>A launcher that is up, a browser session on its website and the page its setup link led to.</summary>
    private sealed record SetupSession(LauncherProcess Launcher, WebSession Web, WebPage Page) : IDisposable
    {
        public void Dispose() => Web.Dispose();
    }

    /// <summary>The owner as a plain setup creates it (name and password).</summary>
    private static Dictionary<string, string> OwnerWithPassword => new()
    {
        ["FirstName"] = "Test", ["LastName"] = "Owner", ["Password"] = Password, ["Confirm"] = Password,
    };

    /// <summary>The owner as a Region Server or Connected Server names it: an avatar that already exists on the other grid.</summary>
    private static Dictionary<string, string> OwnerByName => new() { ["FirstName"] = "Test", ["LastName"] = "Owner" };

    /// <summary>Starts a launcher (<paramref name="which"/> names it in failure messages) and opens its setup link: the plan cards.</summary>
    private async Task<SetupSession> OpenSetupAsync(LauncherPaths paths, LauncherSettings s, string which)
    {
        var launcher = StartLauncher(paths);
        await WaitForAsync(launcher, async () => await GetAsync(s, "?type=Booted") == "ACK", $"{which}: GET /?type=Booted == ACK");
        var web = new WebSession(s.WebPort);
        try
        {
            string link = "";
            await WaitForAsync(launcher, () => Task.FromResult((link = SetupLink(paths)) != ""), $"{which}'s setup link in launcher.log");
            var page = await web.FollowAsync(link);
            Assert.Equal("/setup/plan", page.Path);
            return new SetupSession(launcher, web, page);
        }
        catch
        {
            web.Dispose();
            throw;
        }
    }

    /// <summary>
    /// From the avatar step (<paramref name="page"/>) to the dashboard: the owner (<paramref name="owner"/>), the first
    /// region, its progress until it is ready, and "I'm in!". <paramref name="regionForm"/> picks the region form when the
    /// page has more than one.
    /// </summary>
    private async Task FinishSetupFromAvatarAsync(SetupSession setup, string which, WebPage page, Dictionary<string, string> owner,
        string regionName, string regionForm = "main form")
    {
        var web = setup.Web;
        page = await web.SubmitAsync(page, owner);
        Assert.Equal("/setup/region", page.Path);
        page = await web.SubmitAsync(page, new() { ["RegionName"] = regionName, ["Content"] = "island" }, regionForm);
        Assert.Equal("/setup/region?working=1", page.PathAndQuery);
        await WaitForAsync(setup.Launcher, () => RegionReadyAsync(web), $"{which}: /api/setup/progress to reach \"Ready\"", () => "last progress: " + _lastProgress);
        page = await web.FollowAsync("/setup");
        Assert.Equal("/setup/connect", page.Path);
        page = await web.SubmitAsync(page, new());
        Assert.Equal("/", page.Path);
    }

    private async Task WaitForLoginsEnabledAsync(LauncherProcess launcher, LauncherSettings s, string which) =>
        await WaitForAsync(launcher, async () =>
        {
            _lastStatus = await GetAsync(s, "api/status") ?? "";
            if (_lastStatus == "") return false;
            using var doc = JsonDocument.Parse(_lastStatus);
            return doc.RootElement.GetProperty("grid").GetProperty("loginsEnabled").GetBoolean();
        }, $"{which}: /api/status to report logins enabled", () => "last status: " + _lastStatus);

    /// <summary>A fresh browser signs in to the website at <paramref name="s"/>'s port as the owner, "Test Owner".</summary>
    private static async Task AssertWebsiteSignInAsync(LauncherSettings s)
    {
        using var signIn = new WebSession(s.WebPort);
        var login = await signIn.FollowAsync("/login");
        Assert.Equal("/login", login.Path);
        login = await signIn.SubmitAsync(login, new() { ["Name"] = "Test Owner", ["Password"] = Password });
        Assert.Equal("/", login.Path);
    }

    /// <summary>
    /// Stops <paramref name="a"/>'s world (the grid <paramref name="b"/> depends on), stops <paramref name="b"/>'s too and
    /// starts it again: the start fails, and the dashboard's details for the failed start contain
    /// <paramref name="expectedInDetails"/>.
    /// </summary>
    private async Task AssertStartFailsWhileGridDownAsync(SetupSession a, SetupSession b, string expectedInDetails)
    {
        await GridActionAsync(a.Web, "stop");
        await WaitForAsync(a.Launcher, async () => Stopped(await StatusAsync(a.Web), withGridService: true), "A's world to stop", () => "last status: " + _lastStatus);
        await GridActionAsync(b.Web, "stop");
        await WaitForAsync(b.Launcher, async () => Stopped(await StatusAsync(b.Web), withGridService: false), "B's world to stop", () => "last status: " + _lastStatus);
        await GridActionAsync(b.Web, "start");
        await WaitForAsync(b.Launcher, async () => (await StatusAsync(b.Web)).GetProperty("problem").GetString() == "Your world could not start.",
            "B's start to fail", () => "last status: " + _lastStatus);

        // /api/status gives only the plain sentence; the dashboard's details show the start failure itself.
        var status = await StatusAsync(b.Web);
        Assert.Equal("Failed", status.GetProperty("grid").GetProperty("robust").GetProperty("state").GetString());
        var dashboard = await b.Web.FollowAsync("/");
        var details = new HtmlParser().ParseDocument(dashboard.Html).QuerySelector(".failed-details")?.TextContent ?? "";
        Assert.Contains(expectedInDetails, details);
    }

    /// <summary>Presses a dashboard control (<c>gridAction</c> <paramref name="action"/>) the way a browser without JavaScript does.</summary>
    private static async Task GridActionAsync(WebSession web, string action)
    {
        var dashboard = await web.FollowAsync("/");
        Assert.Equal("/", dashboard.Path);
        var page = await web.SubmitAsync(dashboard, new(), "#controls", button: $"button[name=gridAction][value={action}]");
        Assert.Equal("/", page.Path);
    }

    /// <summary>A signed-in admin's <c>/api/status</c> (kept for failure messages).</summary>
    private async Task<JsonElement> StatusAsync(WebSession web)
    {
        _lastStatus = await web.GetStringAsync("/api/status");
        using var doc = JsonDocument.Parse(_lastStatus);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// True once the database and every region are stopped, and with <paramref name="withGridService"/> this computer's
    /// grid service too (a Region Server's grid service row is the Grid Asset Server on another computer).
    /// </summary>
    private static bool Stopped(JsonElement status, bool withGridService)
    {
        var grid = status.GetProperty("grid");
        static string? State(JsonElement e) => e.GetProperty("state").GetString();
        return State(grid.GetProperty("database")) == "Stopped"
            && (!withGridService || State(grid.GetProperty("robust")) == "Stopped")
            && grid.GetProperty("regions").EnumerateArray().All(r => State(r) == "Stopped");
    }

    private string _lastProgress = "";

    /// <summary>True once the region step reports "Ready"; fails at once when it reports an error.</summary>
    private async Task<bool> RegionReadyAsync(WebSession web)
    {
        var json = await web.GetStringAsync("/api/setup/progress");
        _lastProgress = json;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("error").GetString() is { } error)
            Assert.Fail($"Setting up the region failed: {error}{Environment.NewLine}{root.GetProperty("logExcerpt").GetString()}");
        return root.GetProperty("stage").GetString() == "Ready" && !root.GetProperty("running").GetBoolean();
    }

    /// <summary>The setup link from launcher.log, or "" while it is not written yet.</summary>
    private static string SetupLink(LauncherPaths paths)
    {
        var file = Path.Combine(paths.Logs, "launcher.log");
        if (!File.Exists(file)) return "";
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var match = Regex.Match(reader.ReadToEnd(), @"Setup link: (\S+)");
        return match.Success ? match.Groups[1].Value : "";
    }
}
