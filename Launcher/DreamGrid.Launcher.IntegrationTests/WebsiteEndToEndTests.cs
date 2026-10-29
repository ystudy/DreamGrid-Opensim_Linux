using System.Text.Json;
using System.Text.RegularExpressions;
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

            // Create the region (not Skip) and wait until it is ready (logins enabled).
            page = await web.SubmitAsync(page, new() { ["RegionName"] = "Welcome", ["Content"] = "island" });
            Assert.Equal("/setup/region?working=1", page.PathAndQuery);
            await WaitForAsync(launcher, () => RegionReadyAsync(web), "/api/setup/progress to reach \"Ready\"", () => "last progress: " + _lastProgress);

            var settings = store.LoadOrCreate();
            Assert.False(settings.Hypergrid);
            var region = RegionModel.FindByName(settings, "Welcome")!;
            var ini = IniDocument.Load(Path.Combine(paths.RegionDir(region.Instance), "OpenSim.ini"));
            Assert.Equal("config-include/Grid.ini", ini.Get("Architecture", "Include-Architecture"));
        });
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
