using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DreamGrid.Smoke;

public sealed record SmokeOptions(string Bin, string Physics, string? Culture, int TimeoutSeconds, bool Keep);

/// <summary>
/// Copies bin/ into a sandbox whose path contains a space, writes a standalone configuration, boots OpenSim twice
/// and evaluates the checks C1-C6 (see SmokeChecks and the plan's Task 4).
/// </summary>
public sealed class SmokeRun
{
    const string RegionName = "Smoke Region";

    // Estate keys OpenSimBase reads so the first boot never prompts on the console
    // (OpenSimBase.cs: [Estates] DefaultEstateName / DefaultEstateOwnerName / ...UUID / ...EMail / ...Password).
    // Plan 2's launcher reuses these for non-interactive estate creation.
    const string EstateSection = """
        [Estates]
            DefaultEstateName = Smoke Estate
            DefaultEstateOwnerName = Smoke Owner
            DefaultEstateOwnerUUID = 11111111-2222-3333-4444-555555555555
            DefaultEstateOwnerEMail = smoke@example.invalid
            DefaultEstateOwnerPassword = smoke-password
        """;

    readonly SmokeOptions _opt;
    readonly string _sandboxBin;
    readonly int _httpPort;
    readonly int _regionPort;

    public SmokeRun(SmokeOptions opt)
    {
        _opt = opt;
        string root = Path.Combine(Path.GetTempPath(), $"dg smoke {DateTime.Now:yyyyMMdd-HHmmss}");
        _sandboxBin = Path.Combine(root, "bin");
        _httpPort = FreeTcpPort();
        _regionPort = FreeTcpPort();
    }

    public string SandboxBin => _sandboxBin;

    public async Task<IReadOnlyList<CheckResult>> RunAsync()
    {
        PrepareSandbox();

        var first = await BootAsync();
        var results = new List<CheckResult> { SmokeChecks.LoginsEnabled(first.Output) };
        results.Add(SmokeChecks.FileNonEmpty("C3", Path.Combine(_sandboxBin, "smoke-terrain.png")));
        results.Add(SmokeChecks.DirectoryHasFiles("C4", Path.Combine(_sandboxBin, "assetcache")));
        results.Add(first.SimStatus);

        var second = await BootAsync();
        var again = SmokeChecks.LoginsEnabled(second.Output);
        results.Add(again with { Id = "C6", Detail = "second boot: " + again.Detail });

        string fileLog = File.Exists(LogFile) ? File.ReadAllText(LogFile) : "";
        results.Insert(1, SmokeChecks.NoForbiddenExceptions(first.Output + second.Output + fileLog));

        File.WriteAllText(Path.Combine(_sandboxBin, "smoke.log"), "=== boot 1 ===\n" + first.Output + "\n=== boot 2 ===\n" + second.Output);
        return results;
    }

    string LogFile => Path.Combine(_sandboxBin, "OpenSim.log");

    void PrepareSandbox()
    {
        CopyDirectory(_opt.Bin, _sandboxBin);

        // Gloebit switches itself on whenever Gloebit.dll is present (merging bin/Gloebit.ini, Enabled=True) and then
        // aborts the boot because it needs MySQL. DreamGrid enables it by renaming Gloebit.dll.Proto to Gloebit.dll;
        // version 1 runs with no money module (spec section 4.15), so the sandbox has no Gloebit.dll.
        File.Delete(Path.Combine(_sandboxBin, "Gloebit.dll"));

        string regions = Path.Combine(_sandboxBin, "Regions");
        if (Directory.Exists(regions)) Directory.Delete(regions, true);
        Directory.CreateDirectory(regions);
        File.WriteAllText(Path.Combine(regions, "Regions.ini"), $"""
            [{RegionName}]
                RegionUUID = 6f1f6b1e-0000-4000-8000-00000000d6d6
                Location = 1000,1000
                InternalAddress = 0.0.0.0
                InternalPort = {_regionPort}
                ExternalHostName = 127.0.0.1
            """);

        string include = Path.Combine(_sandboxBin, "config-include");
        File.Copy(Path.Combine(include, "StandaloneCommon.ini.example"), Path.Combine(include, "StandaloneCommon.ini"), true);
        File.Copy(Path.Combine(include, "FlotsamCache.ini.example"), Path.Combine(include, "FlotsamCache.ini"), true);

        File.WriteAllText(Path.Combine(_sandboxBin, "smoke-commands.txt"),
            "terrain save smoke-terrain.png\nterrain load smoke-terrain.png\n");

        // $$ raw string: {{x}} interpolates, so OpenSim's own ${Const|...} expansions stay literal.
        File.WriteAllText(Path.Combine(_sandboxBin, "OpenSim.ini"), $$"""
            [Const]
                BaseHostname = "127.0.0.1"
                BaseURL = http://${Const|BaseHostname}
                PublicPort = "{{_httpPort}}"
                PrivURL = ${Const|BaseURL}
                PrivatePort = "8003"

            [Startup]
                physics = {{_opt.Physics}}
                meshing = {{MesherFor(_opt.Physics)}}
                startup_console_commands_file = smoke-commands.txt

            [Network]
                http_listener_port = {{_httpPort}}

            [Map]
                GenerateMaptiles = true
                MapImageModule = "Warp3DImageModule"

            [RegionReady]
                enabled = true

            ; Version 1 runs with the old Wifi off (spec section 4.8). Having this section also stops WifiModule from
            ; merging bin/Wifi.ini, which would switch [DatabaseService] to Diva.Data.MySQL.
            [WifiService]
                Enabled = false

            {{EstateSection}}

            [Modules]
                AssetCaching = "FlotsamAssetCache"
                Include-FlotsamCache = "config-include/FlotsamCache.ini"

            [Architecture]
                Include-Architecture = "config-include/Standalone.ini"
            """);
    }

    // BulletSim refuses to start unless meshing is "Meshmerizer" (BSScene.Initialise); ubODE uses its own mesher.
    static string MesherFor(string physics) =>
        physics.Equals("BulletSim", StringComparison.OrdinalIgnoreCase) ? "Meshmerizer" : "ubODEMeshmerizer";

    sealed record Boot(string Output, CheckResult SimStatus);

    async Task<Boot> BootAsync()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _sandboxBin,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("OpenSim.dll");
        psi.ArgumentList.Add("-console=basic");
        if (_opt.Culture is { } culture)
        {
            psi.Environment["LC_ALL"] = culture;
            psi.Environment["LANG"] = culture;
        }

        var output = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        string Snapshot() { lock (output) return output.ToString(); }

        var deadline = DateTime.UtcNow.AddSeconds(_opt.TimeoutSeconds);
        while (DateTime.UtcNow < deadline && !proc.HasExited && !SmokeChecks.LoginsEnabled(Snapshot()).Passed)
            await Task.Delay(1000);

        // The startup commands run after the region is up; give the terrain save/load time to finish.
        string png = Path.Combine(_sandboxBin, "smoke-terrain.png");
        var pngDeadline = DateTime.UtcNow.AddSeconds(30);
        while (!proc.HasExited && DateTime.UtcNow < pngDeadline && !File.Exists(png))
            await Task.Delay(500);
        await Task.Delay(2000);

        CheckResult simStatus = await SimStatusAsync();

        if (!proc.HasExited)
        {
            try { await proc.StandardInput.WriteLineAsync("quit"); await proc.StandardInput.FlushAsync(); } catch (IOException) { }
            using var quitWait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await proc.WaitForExitAsync(quitWait.Token); }
            catch (OperationCanceledException) { proc.Kill(entireProcessTree: true); await proc.WaitForExitAsync(); }
        }
        return new Boot(Snapshot(), simStatus);
    }

    async Task<CheckResult> SimStatusAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string body = (await http.GetStringAsync($"http://127.0.0.1:{_httpPort}/simstatus/")).Trim();
            return new CheckResult("C5", body == "OK", $"/simstatus/ returned '{body}'");
        }
        catch (Exception e)
        {
            return new CheckResult("C5", false, "/simstatus/ failed: " + e.Message);
        }
    }

    static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // Skips state a previous run may have left in bin/: the Mono.Addins registry, the asset cache, logs, databases.
    static bool Skip(string name, bool isDir) =>
        isDir ? name.StartsWith("addin-db-", StringComparison.OrdinalIgnoreCase) || name.Equals("assetcache", StringComparison.OrdinalIgnoreCase)
              : name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from))
            if (!Skip(Path.GetFileName(dir), true))
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        foreach (var file in Directory.EnumerateFiles(from))
            if (!Skip(Path.GetFileName(file), false))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
    }
}
