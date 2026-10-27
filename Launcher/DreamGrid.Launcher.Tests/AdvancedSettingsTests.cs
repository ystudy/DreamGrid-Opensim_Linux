using System.Net;
using System.Net.Sockets;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class AdvancedSettingsTests : IAsyncLifetime
{
    private static readonly int FreePort = PickFreePort();

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        Programs = TestSettings.ProgramFor(new RegionSettings("Welcome", "Welcome", Guid.NewGuid())), Regions = TestSettings.RegionsFor(new RegionSettings("Welcome", "Welcome", Guid.NewGuid())),
        // Nothing listens here, so "Test connection" fails at once.
        MariaDbPort = FreePort,
    })
    {
        Before = TestAuth.Apply,
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int PickFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    private static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    private static string Token(string html) =>
        Parse(html).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

    /// <summary>The text a person sees in the main area.</summary>
    private static string Text(string html) => Parse(html).QuerySelector("main")!.TextContent;

    /// <summary>The Network form as it is now, with <paramref name="changes"/> applied, plus the antiforgery token.</summary>
    private static async Task<HttpResponseMessage> PostNetworkAsync(HttpClient client, Dictionary<string, string> changes)
    {
        var page = await client.GetStringAsync("/advanced/network", Ct);
        var doc = Parse(page);
        var form = new Dictionary<string, string>();
        foreach (var input in doc.QuerySelectorAll("form#network input"))
            form[input.GetAttribute("name")!] = input.GetAttribute("value") ?? "";
        foreach (var (k, v) in changes) form[k] = v;
        return await client.PostAsync("/advanced/network", new FormUrlEncodedContent(form), Ct);
    }

    private byte[] SettingsBytes() => File.ReadAllBytes(_f.Paths.SettingsFile);

    [Fact]
    public async Task Advanced_pages_need_the_admin_role()
    {
        using var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        foreach (var url in new[] { "/advanced", "/advanced/network", "/advanced/database", "/advanced/module/loglevel" })
        {
            using var resp = await client.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            Assert.StartsWith("/login", resp.Headers.Location!.PathAndQuery);
        }
    }

    [Fact]
    public async Task Advanced_menu_lists_module_pages()
    {
        using var client = await SignedInAsync(_f);
        var doc = Parse(await client.GetStringAsync("/advanced", Ct));

        var links = doc.QuerySelectorAll("main a").Select(a => (a.TextContent.Trim(), a.GetAttribute("href"))).ToList();
        Assert.Equal(
            [("Network", "/advanced/network"), ("Database", "/advanced/database"), ("Log level", "/advanced/module/loglevel"), ("Logs", "/logs")],
            links);
    }

    [Fact]
    public async Task Network_page_shows_the_hostname_and_every_port()
    {
        using var client = await SignedInAsync(_f);
        var doc = Parse(await client.GetStringAsync("/advanced/network", Ct));

        var host = doc.QuerySelector("input[name=Hostname]")!;
        Assert.Equal("", host.GetAttribute("value") ?? "");
        Assert.Equal(HostAddress.Detect(), host.GetAttribute("placeholder"));
        var expected = new Dictionary<string, string>
        {
            ["WebPort"] = "8001", ["RobustPublicPort"] = "8002", ["RobustPrivatePort"] = "8003", ["RegionPort"] = "8004",
            ["MariaDbPort"] = FreePort.ToString(), ["RobustConsolePort"] = "8010", ["RegionConsolePort"] = "8011",
        };
        foreach (var (name, value) in expected)
            Assert.Equal(value, doc.QuerySelector($"input[name={name}]")?.GetAttribute("value"));
        Assert.Contains("Robust public port", doc.QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task Network_save_with_bad_value_leaves_settings_file_untouched()
    {
        using var client = await SignedInAsync(_f);
        var before = SettingsBytes();

        foreach (var bad in new Dictionary<string, string>[]
        {
            new() { ["WebPort"] = "0" },
            new() { ["RegionPort"] = "8001" },
            new() { ["Hostname"] = "my host" },
            new() { ["RobustConsolePort"] = "eighty" },
        })
        {
            using var resp = await PostNetworkAsync(client, bad);
            var html = await resp.Content.ReadAsStringAsync(Ct);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var field = bad.Keys.Single();
            var error = Parse(html).QuerySelector($"[data-error-for={field}]");
            Assert.False(string.IsNullOrWhiteSpace(error?.TextContent), $"no error shown for {field}");
            Assert.DoesNotContain("Restart your world", html);
        }

        Assert.Equal(before, SettingsBytes());
        Assert.Empty(_f.Grid.UpdateWriteConfig);
    }

    [Fact]
    public async Task Network_save_with_good_values_saves_and_regenerates()
    {
        using var client = await SignedInAsync(_f);

        using var resp = await PostNetworkAsync(client, new() { ["Hostname"] = "world.example.com", ["RegionPort"] = "9004" });
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal([true], _f.Grid.UpdateWriteConfig);
        Assert.DoesNotContain("RegenerateConfig", _f.Grid.Snapshot());
        var saved = _f.Store.LoadOrCreate();
        Assert.Equal("world.example.com", saved.Hostname);
        Assert.Equal(9004, saved.RegionPort);
        Assert.Contains("Saved. Restart your world to use the new settings.", Text(html));
        // The restart reuses the dashboard's control, as a plain form with the antiforgery token.
        var restart = Parse(html).QuerySelector("form[action='/dashboard?handler=run']")!;
        Assert.NotNull(restart.QuerySelector("input[name=__RequestVerificationToken]"));
        Assert.NotNull(restart.QuerySelector("button[name=gridAction][value=restart]"));
        // Only the region port changed: no note about restarting the launcher itself.
        Assert.DoesNotContain("launcher itself", Text(html));
    }

    [Fact]
    public async Task Network_save_with_an_empty_hostname_means_automatic()
    {
        _f.Store.Save(_f.Store.LoadOrCreate() with { Hostname = "old.example.com" });
        using var client = await SignedInAsync(_f);

        (await PostNetworkAsync(client, new() { ["Hostname"] = "" })).Dispose();

        Assert.Null(_f.Store.LoadOrCreate().Hostname);
    }

    [Fact]
    public async Task Network_save_explains_a_changed_web_port_but_not_a_robust_port()
    {
        using var client = await SignedInAsync(_f);

        using var resp = await PostNetworkAsync(client, new() { ["WebPort"] = "9001", ["RobustPublicPort"] = "9002" });
        var text = Text(await resp.Content.ReadAsStringAsync(Ct));

        Assert.Contains("restart the launcher itself", text);
        Assert.Contains(":9001/", text);
        Assert.DoesNotContain("keeps talking to Robust", text);
    }

    [Fact]
    public async Task Restart_button_after_saving_restarts_the_world()
    {
        using var client = await SignedInAsync(_f);
        using var saved = await PostNetworkAsync(client, new() { ["RegionPort"] = "9004" });
        var token = Token(await saved.Content.ReadAsStringAsync(Ct));
        await _f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); // the host's own start at launch

        using var resp = await client.PostAsync("/dashboard?handler=run", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["gridAction"] = "restart",
            ["__RequestVerificationToken"] = token,
        }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        for (var i = 0; i < 100 && !_f.Grid.Snapshot().Contains("StopAllAsync"); i++) await Task.Delay(50, Ct);
        Assert.Contains("StopAllAsync", _f.Grid.Snapshot());
    }

    [Fact]
    public async Task Database_page_masks_passwords_by_default()
    {
        var s = _f.Store.LoadOrCreate();
        using var client = await SignedInAsync(_f);

        var html = await client.GetStringAsync("/advanced/database", Ct);

        Assert.DoesNotContain(s.Db.RobustPassword, html);
        Assert.DoesNotContain(s.Db.OpensimPassword, html);
        Assert.DoesNotContain(s.Db.RootPassword, html);
        var text = Text(html);
        foreach (var expected in new[] { "127.0.0.1", FreePort.ToString(), "robustuser", "opensimuser", "••••" })
            Assert.Contains(expected, text);
    }

    [Fact]
    public async Task Database_show_reveals_passwords_only_on_a_post_with_the_token()
    {
        var s = _f.Store.LoadOrCreate();
        using var client = await SignedInAsync(_f);
        var token = Token(await client.GetStringAsync("/advanced/database", Ct));

        using var noToken = await client.PostAsync("/advanced/database?handler=show", new FormUrlEncodedContent([]), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        using var resp = await client.PostAsync("/advanced/database?handler=show",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);
        var text = Text(await resp.Content.ReadAsStringAsync(Ct));

        Assert.Contains(s.Db.RobustPassword, text);
        Assert.Contains(s.Db.OpensimPassword, text);
    }

    [Fact]
    public async Task Database_test_connection_shows_the_error_when_it_cannot_connect()
    {
        using var client = await SignedInAsync(_f);
        var token = Token(await client.GetStringAsync("/advanced/database", Ct));

        using var resp = await client.PostAsync("/advanced/database?handler=test",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var box = Parse(html).QuerySelector("#test-result.error-box");
        Assert.NotNull(box);
        Assert.Contains("Could not connect", box.TextContent);
        Assert.DoesNotContain(_f.Store.LoadOrCreate().Db.RobustPassword, html);
    }

    [Fact]
    public async Task Module_page_renders_LogLevel_and_saves_choice()
    {
        using var client = await SignedInAsync(_f);
        var page = await client.GetStringAsync("/advanced/module/loglevel", Ct);
        var select = Parse(page).QuerySelector("select[name=level]");
        Assert.NotNull(select);
        Assert.Equal(["DEBUG", "INFO", "WARN", "ERROR"], select.QuerySelectorAll("option").Select(o => o.GetAttribute("value")));
        Assert.Equal("INFO", select.QuerySelector("option[selected]")?.GetAttribute("value"));

        using var resp = await client.PostAsync("/advanced/module/loglevel", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["level"] = "DEBUG",
            ["__RequestVerificationToken"] = Token(page),
        }), Ct);
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("DEBUG", _f.Store.LoadOrCreate().Modules["loglevel"]["level"]);
        Assert.Equal([true], _f.Grid.UpdateWriteConfig);
        Assert.Contains("Restart your world", Text(html));
        Assert.Equal("DEBUG", Parse(html).QuerySelector("select[name=level] option[selected]")?.GetAttribute("value"));
    }

    [Fact]
    public async Task Module_page_rejects_an_unknown_level_and_leaves_settings_untouched()
    {
        using var client = await SignedInAsync(_f);
        var page = await client.GetStringAsync("/advanced/module/loglevel", Ct);
        var before = SettingsBytes();

        using var resp = await client.PostAsync("/advanced/module/loglevel", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["level"] = "TRACE",
            ["__RequestVerificationToken"] = Token(page),
        }), Ct);
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(Parse(html).QuerySelector("[data-error-for=level]")?.TextContent));
        Assert.Equal(before, SettingsBytes());
        Assert.Empty(_f.Grid.UpdateWriteConfig);
    }

    [Fact]
    public async Task Module_page_for_an_unknown_module_is_404()
    {
        using var client = await SignedInAsync(_f);

        using var resp = await client.GetAsync("/advanced/module/nope", Ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Dashboard_has_no_features_area_without_switches()
    {
        using var client = await SignedInAsync(_f);
        var doc = Parse(await client.GetStringAsync("/", Ct));

        Assert.Null(doc.QuerySelector("#features"));
    }

    [Fact]
    public async Task Dashboard_features_area_renders_and_toggles_a_registered_switch()
    {
        await using var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IDashboardSwitch, TestSwitch>(),
        };
        using var client = await SignedInAsync(f);
        var page = await client.GetStringAsync("/", Ct);
        var features = Parse(page).QuerySelector("#features");
        Assert.NotNull(features);
        Assert.Contains("Test birds", features.TextContent);
        Assert.Contains("Off", features.TextContent);
        var original = f.Grid.CurrentSettings();

        using var resp = await client.PostAsync("/dashboard?handler=switch", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["moduleId"] = "testbirds",
            ["on"] = "true",
            ["__RequestVerificationToken"] = Token(page),
        }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal([true], f.Grid.UpdateWriteConfig);
        Assert.Equal("true", f.Store.LoadOrCreate().Modules["testbirds"]["on"]);
        Assert.False(original.Modules.ContainsKey("testbirds"));
        var after = Parse(await client.GetStringAsync("/", Ct)).QuerySelector("#features")!;
        Assert.Contains("On", after.TextContent);
        Assert.NotNull(after.QuerySelector("button[name=on][value=false]"));
    }

    private sealed class TestSwitch : IDashboardSwitch
    {
        public string ModuleId => "testbirds";
        public string Label => "Test birds";
        public string Description => "Birds fly over your region.";

        public bool IsOn(LauncherSettings s) =>
            s.Modules.TryGetValue(ModuleId, out var m) && m.GetValueOrDefault("on") == "true";

        public LauncherSettings Toggle(LauncherSettings s, bool on) => s with
        {
            Modules = new(s.Modules) { [ModuleId] = new() { ["on"] = on ? "true" : "false" } },
        };
    }

    // ---- region ports (plan 4a) ----

    private void AddIsle() => _f.Store.Save(_f.Store.LoadOrCreate() is var s ? s with
    {
        Programs = [.. s.Programs, new ProgramSettings("Isle", 8006, 8012)],
        Regions = [.. s.Regions, new RegionSettings("Isle", "Isle", Guid.NewGuid(), 1010, 1010, UdpPort: 8006)],
    } : null!);

    [Fact]
    public async Task Network_refuses_a_port_a_region_uses()
    {
        AddIsle();
        using var client = await SignedInAsync(_f);
        var before = SettingsBytes();

        using var resp = await PostNetworkAsync(client, new() { ["RobustPublicPort"] = "8006" });
        var error = Parse(await resp.Content.ReadAsStringAsync(Ct)).QuerySelector("[data-error-for=RobustPublicPort]");

        Assert.Contains("used by region Isle", error?.TextContent);
        Assert.Equal(before, SettingsBytes());
    }

    [Fact]
    public async Task Network_page_lists_region_ports()
    {
        AddIsle();
        using var client = await SignedInAsync(_f);
        var doc = Parse(await client.GetStringAsync("/advanced/network", Ct));

        var rows = doc.QuerySelectorAll("#region-ports tbody tr").Select(r => r.TextContent).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains("8006", rows[1]);
        Assert.Contains("8012", rows[1]);
        Assert.Contains("First region port", doc.QuerySelector("main")!.TextContent);
    }
}
