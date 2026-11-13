using Xunit;
using System.Diagnostics;
using System.Reflection;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.ModelImport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridGate.Launcher.Tests;

public sealed class ModelConverterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-converter-" + Guid.NewGuid().ToString("N"));
    private readonly string _job;
    private ProcessStartInfo? _seen;
    private static readonly ImportLimits Limits = new(256, 0.001, 10, 0, 67108864, 8192, 1024, 2000000, 512, 200000, 67108864);
    private static readonly ImportOptions Options = new("Chair", "chair.glb", 100, new NextOwner(true, true, false), Guid.NewGuid(), "Test Person");

    public ModelConverterTests()
    {
        _job = Path.Combine(_root, "job");
        Directory.CreateDirectory(_job);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string FakeChildExe()
    {
        var dir = typeof(ModelConverterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "FakeChildDir").Value!;
        return Path.Combine(dir, OperatingSystem.IsWindows() ? "GridGate.FakeChild.exe" : "GridGate.FakeChild");
    }

    /// <summary>A converter whose child is FakeChild with <paramref name="fakeArgs"/>; the real start info is kept in <c>_seen</c>.</summary>
    private ModelConverter Converter(string[] fakeArgs, TimeSpan? limit = null) =>
        new(new LauncherPaths(_root, _root, null), TimeProvider.System, NullLogger<ModelConverter>.Instance, limit)
        {
            StartForTests = real =>
            {
                _seen = real;
                var psi = new ProcessStartInfo(FakeChildExe()) { UseShellExecute = false };
                foreach (var a in fakeArgs) psi.ArgumentList.Add(a);
                foreach (var kv in real.Environment) psi.Environment[kv.Key] = kv.Value;
                return psi;
            },
        };

    private Task<ConverterRun> Check(ModelConverter c) =>
        c.CheckAsync(Path.Combine(_job, "in.glb"), Limits, Options, _job, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Converter_report_is_read()
    {
        var run = await Check(Converter(["--import-ok", _job]));
        Assert.Equal(ConverterOutcome.Done, run.Outcome);
        Assert.NotNull(run.Report);
        Assert.True(run.Report!.Blocked);
        // The launcher wrote the converter's inputs, and set the memory limit and the command line.
        Assert.True(File.Exists(Path.Combine(_job, "limits.json")));
        Assert.True(File.Exists(Path.Combine(_job, "options.json")));
        Assert.Equal("0x60000000", _seen!.Environment["DOTNET_GCHeapHardLimit"]);
        Assert.EndsWith("GridGate.ModelImport.dll", _seen.ArgumentList[0]);
        Assert.Equal("check", _seen.ArgumentList[1]);
    }

    [Fact]
    public async Task Converter_failure_is_failed()
    {
        var run = await Check(Converter(["--crash"]));
        Assert.Equal(ConverterOutcome.Failed, run.Outcome);
        Assert.Null(run.Report);
    }

    [Fact]
    public async Task Converter_exit_zero_without_a_report_is_failed()
    {
        var run = await Check(Converter(["--import-ok", Path.Combine(_root, "elsewhere")]));
        Assert.Equal(ConverterOutcome.Failed, run.Outcome);
        Assert.Null(run.Report);
    }

    [Fact]
    public async Task Converter_runs_below_normal_priority()
    {
        var pidFile = Path.Combine(_root, "slow.pid");
        var run = Check(Converter(["--import-hang", pidFile], TimeSpan.FromSeconds(5)));
        ProcessPriorityClass? priority = null;
        try
        {
            int pid = 0;
            for (var i = 0; i < 100 && !(File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out pid)); i++)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.NotEqual(0, pid);
            using var p = Process.GetProcessById(pid);
            priority = p.PriorityClass;
        }
        finally
        {
            // The time limit stops the child whatever the asserts did, so it never outlives the test run.
            Assert.Equal(ConverterOutcome.TimedOut, (await run).Outcome);
        }
        Assert.Equal(ProcessPriorityClass.BelowNormal, priority);
    }

    [Fact]
    public async Task Converter_hang_is_killed()
    {
        var pidFile = Path.Combine(_root, "hang.pid");
        var run = await Check(Converter(["--import-hang", pidFile], TimeSpan.FromSeconds(1)));
        Assert.Equal(ConverterOutcome.TimedOut, run.Outcome);
        Assert.Null(run.Report);

        var pid = int.Parse(File.ReadAllText(pidFile));
        var gone = false;
        for (var i = 0; i < 50 && !gone; i++)
        {
            try { using var p = Process.GetProcessById(pid); gone = p.HasExited; }
            catch (ArgumentException) { gone = true; }
            if (!gone) await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.True(gone, "the hung converter is still running");
    }
}
