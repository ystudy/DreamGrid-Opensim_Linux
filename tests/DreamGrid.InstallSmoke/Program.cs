using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DreamGrid.Launcher.IntegrationTests;

// Usage: DreamGrid.InstallSmoke --url <base> --setup-link <link> --data <dir> --robust-port <n> --stop "<cmd>" --start "<cmd>"
// Drives an installed DreamGrid from the installer's setup link to a running region, then stops and starts the
// service and checks that the region kept running and was adopted. One line per stage; exit 0 when every stage
// passed, 1 at the first failing stage, 2 for bad arguments.

const string World = "Smoke World";
const string Region = "Smoke Island";
const string RegionFolder = "Smoke_Island"; // the launcher's SafeFolderName(Region)
var readyTimeout = TimeSpan.FromMinutes(10);

var options = new Dictionary<string, string>();
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i == args.Length - 1)
        return Usage($"unexpected argument: {args[i]}");
    options[args[i][2..]] = args[++i];
}
string[] required = ["url", "setup-link", "data", "robust-port", "stop", "start"];
if (required.FirstOrDefault(r => !options.ContainsKey(r)) is { } missing) return Usage($"--{missing} is required");
if (!Uri.TryCreate(options["url"], UriKind.Absolute, out var baseUri)) return Usage("--url must be an absolute URL");
if (!int.TryParse(options["robust-port"], out var robustPort)) return Usage("--robust-port must be a number");
if (!Uri.TryCreate(options["setup-link"], UriKind.Absolute, out var setupLink)) return Usage("--setup-link must be an absolute URL");

var pidFile = Path.Combine(options["data"], "state", "pids", RegionFolder + ".pid");
var stage = "setup";
try
{
    using var web = new WebSession(baseUri.Port);

    // 1. The setup steps. The link names the machine's LAN address; follow its path on --url so the cookies
    // stay with the one host the rest of the run talks to. The Local plan, then its network step (no fields).
    var password = "Smoke-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    var page = await web.FollowAsync(setupLink.PathAndQuery);
    Expect("/setup/plan", page.Path);
    page = await web.FollowAsync("/setup/plan/local");
    page = await web.SubmitAsync(page, new());
    Expect("/setup/network", page.Path);
    page = await web.SubmitAsync(page, new());
    Expect("/setup/world", page.Path);
    page = await web.SubmitAsync(page, new() { ["GridName"] = World });
    Expect("/setup/avatar", page.Path);
    page = await web.SubmitAsync(page, new()
    {
        ["FirstName"] = "Smoke", ["LastName"] = "Tester", ["Password"] = password, ["Confirm"] = password,
    });
    Expect("/setup/region", page.Path);
    page = await web.SubmitAsync(page, new() { ["RegionName"] = Region, ["Content"] = "island" });
    Expect("/setup/region?working=1", page.PathAndQuery);
    await WaitAsync(() => RegionReadyAsync(web), readyTimeout, "the region step to reach \"Ready\"");
    page = await web.FollowAsync("/setup");
    Expect("/setup/connect", page.Path);
    if (!page.Html.Contains($":{robustPort}", StringComparison.Ordinal))
        throw new SmokeFailure($"the connect step does not show the login port {robustPort}");
    page = await web.SubmitAsync(page, new());
    Expect("/", page.Path);
    Pass("setup", "plan, network, world, owner, region and connect steps done");

    // 2. Logins open, as an admin sees it.
    stage = "logins";
    await WaitAsync(() => LoginsEnabledAsync(web), readyTimeout, "/api/status to report logins enabled");
    Pass(stage, "logins enabled");

    // 3. The grid service answers on its public port with the new world's name.
    stage = "grid-info";
    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    {
        var info = await http.GetStringAsync($"http://127.0.0.1:{robustPort}/get_grid_info");
        if (!info.Contains(World, StringComparison.Ordinal))
            throw new SmokeFailure($"get_grid_info does not name \"{World}\": {info}");
    }
    Pass(stage, $"port {robustPort} names \"{World}\"");

    // 4. The region's PID.
    stage = "region-pid";
    var pid = ReadPid(pidFile);
    if (!IsAlive(pid)) throw new SmokeFailure($"PID {pid} from {pidFile} is not running");
    Pass(stage, $"PID {pid}");

    // 5. Stopping the service leaves the region running.
    stage = "stop";
    await RunAsync(options["stop"]);
    if (!IsAlive(pid)) throw new SmokeFailure($"the region (PID {pid}) ended with the service");
    Pass(stage, $"service stopped, region PID {pid} still running");

    // 6. Starting it again adopts the region instead of starting another one.
    stage = "start";
    await RunAsync(options["start"]);
    await WaitAsync(() => LoginsEnabledAsync(web), readyTimeout, "/api/status to report logins enabled after the start");
    var after = ReadPid(pidFile);
    if (after != pid) throw new SmokeFailure($"the region PID changed from {pid} to {after}: it was restarted, not adopted");
    Pass(stage, $"service started, region PID {pid} adopted");
    Console.WriteLine("InstallSmoke: all stages passed");
    return 0;
}
catch (Exception e)
{
    Console.WriteLine($"FAIL {stage}: {e.Message}");
    return 1;
}

static int Usage(string error)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("usage: DreamGrid.InstallSmoke --url <base> --setup-link <link> --data <dir> --robust-port <n> --stop \"<cmd>\" --start \"<cmd>\"");
    return 2;
}

static void Pass(string stage, string detail) => Console.WriteLine($"OK   {stage}: {detail}");

static void Expect(string expected, string actual)
{
    if (actual != expected) throw new SmokeFailure($"expected to be on {expected}, but landed on {actual}");
}

/// <summary>Polls <paramref name="check"/> every 2 s until it returns true; an exception from it ends the wait at once.</summary>
static async Task WaitAsync(Func<Task<bool>> check, TimeSpan timeout, string what)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!await check())
    {
        if (DateTime.UtcNow >= deadline) throw new SmokeFailure($"timed out after {timeout.TotalMinutes:0} min waiting for {what}");
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}

/// <summary>True once the region step reports "Ready"; fails at once when it reports an error.</summary>
static async Task<bool> RegionReadyAsync(WebSession web)
{
    using var doc = JsonDocument.Parse(await web.GetStringAsync("/api/setup/progress"));
    var root = doc.RootElement;
    if (root.GetProperty("error").GetString() is { } error)
        throw new SmokeFailure($"setting up the region failed: {error}{Environment.NewLine}{root.GetProperty("logExcerpt").GetString()}");
    return root.GetProperty("stage").GetString() == "Ready" && !root.GetProperty("running").GetBoolean();
}

/// <summary>
/// True once the signed-in admin's status shows logins enabled. False while the launcher is not answering yet (the
/// service is starting); a status that no longer knows the admin fails, since the sign-in must survive a restart.
/// </summary>
static async Task<bool> LoginsEnabledAsync(WebSession web)
{
    string json;
    try { json = await web.GetStringAsync("/api/status"); }
    catch (HttpRequestException) { return false; }
    using var doc = JsonDocument.Parse(json);
    if (!doc.RootElement.GetProperty("admin").GetBoolean())
        throw new SmokeFailure("/api/status does not see the signed-in owner as an admin");
    return doc.RootElement.GetProperty("grid").GetProperty("loginsEnabled").GetBoolean();
}

static int ReadPid(string file)
{
    var text = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
    return int.TryParse(text, out var pid) && pid > 0 ? pid : throw new SmokeFailure($"no PID in {file}");
}

/// <summary>
/// True while the process exists. The region runs as the service's account, which an unelevated smoke may not inspect:
/// access denied (Win32Exception) means it exists, so it counts as alive; a process that is not there (ArgumentException)
/// or that exited while being looked at (InvalidOperationException) counts as gone.
/// </summary>
static bool IsAlive(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (ArgumentException) { return false; }
    catch (InvalidOperationException) { return false; }
    catch (System.ComponentModel.Win32Exception) { return true; }
}

/// <summary>Runs a service command through the shell; a non-zero exit fails the stage with its output.</summary>
static async Task RunAsync(string command)
{
    var psi = OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", command } }
        : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
    psi.RedirectStandardOutput = psi.RedirectStandardError = true;
    using var process = Process.Start(psi)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
        throw new SmokeFailure($"\"{command}\" exited with {process.ExitCode}: {(await stdout + await stderr).Trim()}");
}

/// <summary>A failed check, reported with its stage.</summary>
sealed class SmokeFailure(string message) : Exception(message);
