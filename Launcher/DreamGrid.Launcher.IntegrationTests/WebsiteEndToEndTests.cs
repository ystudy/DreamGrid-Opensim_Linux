using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
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

            // 2. The link sets the setup cookie and lands on the first step.
            var page = await web.FollowAsync(link);
            Assert.Equal("/setup/world", page.Path);

            // 3. Step 1: the world's name.
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

    /// <summary>A page reached after following every redirect.</summary>
    private sealed record WebPage(Uri Uri, string Html)
    {
        public string Path => Uri.AbsolutePath;
        public string PathAndQuery => Uri.PathAndQuery;
    }

    /// <summary>
    /// A browser stand-in: keeps cookies, follows redirects itself (so each hop's cookies are kept), and submits a
    /// page's form with the fields the page really has, including its antiforgery token.
    /// </summary>
    private sealed class WebSession : IDisposable
    {
        private const int MaxRedirects = 10;
        private readonly HttpClient _client;
        private readonly HtmlParser _parser = new();

        public WebSession(int port)
        {
            var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };
            // Step 2 starts the database and Robust within the request when they are not running yet.
            _client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes(5) };
        }

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public async Task<string> GetStringAsync(string url)
        {
            using var response = await _client.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        public async Task<WebPage> FollowAsync(string url) => await FollowAsync(await _client.GetAsync(url, Ct));

        /// <summary>
        /// Posts the page's form (<paramref name="selector"/>, by default the one in &lt;main&gt;) with its own
        /// values, overridden by <paramref name="fields"/>; every field must exist in the form.
        /// </summary>
        public async Task<WebPage> SubmitAsync(WebPage page, Dictionary<string, string> fields, string selector = "main form")
        {
            var doc = await _parser.ParseDocumentAsync(page.Html, Ct);
            var form = doc.QuerySelector(selector);
            Assert.True(form is not null, $"{page.Path} has no form matching \"{selector}\"");
            Assert.Equal("post", form!.GetAttribute("method"), StringComparer.OrdinalIgnoreCase);

            var values = new List<KeyValuePair<string, string>>();
            var names = new HashSet<string>();
            foreach (var input in form.QuerySelectorAll("input[name], select[name], textarea[name]"))
            {
                var name = input.GetAttribute("name")!;
                names.Add(name);
                if (fields.ContainsKey(name)) continue;
                var type = input.GetAttribute("type")?.ToLowerInvariant();
                if (type is "radio" or "checkbox" && !input.HasAttribute("checked")) continue;
                values.Add(new(name, input.GetAttribute("value") ?? ""));
            }
            Assert.Contains("__RequestVerificationToken", names);
            foreach (var (name, value) in fields)
            {
                Assert.True(names.Contains(name), $"the form on {page.Path} has no field \"{name}\"");
                values.Add(new(name, value));
            }

            var action = new Uri(page.Uri, form.GetAttribute("action") is { Length: > 0 } a ? a : page.PathAndQuery);
            using var content = new FormUrlEncodedContent(values);
            var response = await _client.PostAsync(action, content, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect,
                $"POST {action.PathAndQuery} answered {(int)response.StatusCode}, not a redirect:{Environment.NewLine}{ErrorText(await response.Content.ReadAsStringAsync(Ct))}");
            return await FollowAsync(response);
        }

        private async Task<WebPage> FollowAsync(HttpResponseMessage first)
        {
            var response = first;
            try
            {
                for (var hop = 0; ; hop++)
                {
                    var uri = response.RequestMessage!.RequestUri!;
                    if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.SeeOther))
                    {
                        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {uri.PathAndQuery} answered {(int)response.StatusCode}");
                        return new WebPage(uri, await response.Content.ReadAsStringAsync(Ct));
                    }
                    Assert.True(hop < MaxRedirects, $"more than {MaxRedirects} redirects, the last to {response.Headers.Location}");
                    var next = new Uri(uri, response.Headers.Location!);
                    response.Dispose();
                    response = await _client.GetAsync(next, Ct);
                }
            }
            finally { response.Dispose(); }
        }

        /// <summary>A page's error boxes and field errors, so a refused form says why.</summary>
        private string ErrorText(string html)
        {
            var doc = _parser.ParseDocument(html);
            var errors = doc.QuerySelectorAll(".error-box, .field-error").Select(e => e.TextContent.Trim()).Where(t => t != "");
            return string.Join(Environment.NewLine, errors);
        }

        public void Dispose() => _client.Dispose();
    }
}
