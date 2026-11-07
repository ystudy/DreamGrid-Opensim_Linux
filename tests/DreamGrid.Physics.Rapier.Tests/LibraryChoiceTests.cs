using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using OpenMetaverse;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// Which of the two shipped builds is loaded: "rapier" (no parallel feature) for one thread, "rapier_mt" (parallel)
/// for more, and "rapier" at one thread when "rapier_mt" cannot be loaded.
/// </summary>
public class LibraryChoiceTests
{
    /// <summary>
    /// The library is loaded once per process. By default the run uses the single-threaded build, as a region with the
    /// default RapierNumThreads (1) does. RAPIER_TEST_THREADS=2 runs every test against "rapier_mt" instead.
    /// </summary>
    [ModuleInitializer]
    internal static void LoadTheBuildForThisRun()
    {
        int threads = int.TryParse(Environment.GetEnvironmentVariable("RAPIER_TEST_THREADS"), out int n) ? n : 1;
        RapierNative.TryLoad(threads, out _, out _);
    }

    [Fact]
    public void TheRunUsesTheBuildItAskedFor()
    {
        int threads = int.TryParse(Environment.GetEnvironmentVariable("RAPIER_TEST_THREADS"), out int n) ? n : 1;
        Assert.True(RapierNative.TryLoad(threads, out string? error, out _), error);
        Assert.Equal(threads > 1, RapierNative.r3BuildFeatures().parallel != 0);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate RapierNative.BuildFeatures BuildFeaturesFn();

    private static bool IsParallel(IntPtr library)
    {
        IntPtr fn = NativeLibrary.GetExport(library, "r3BuildFeatures");
        return Marshal.GetDelegateForFunctionPointer<BuildFeaturesFn>(fn)().parallel != 0;
    }

    // A dllmap line whose file is missing: what LoadDll reports for rapier_mt when librapier-mt-*.so is not shipped.
    private static IntPtr LoadMissing()
    {
        return NativeLibrary.TryLoad(Path.Combine(Path.GetTempPath(), "no-such-dir", "no-such-rapier-mt"), out IntPtr h)
            ? h : IntPtr.Zero;
    }

    // The file the dllmap lines name for this machine: rapier(-mt).dll on Windows, librapier(-mt)-<cpu>.so elsewhere.
    private static string ShippedFileName(bool mt)
    {
        if (OperatingSystem.IsWindows())
            return mt ? "rapier-mt.dll" : "rapier.dll";
        string cpu = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "arm64",
            Architecture other => throw new PlatformNotSupportedException($"no Rapier library is shipped for {other}"),
        };
        return (mt ? "librapier-mt-" : "librapier-") + cpu + ".so";
    }

    [Theory]
    [InlineData(0, "rapier")]
    [InlineData(1, "rapier")]
    [InlineData(2, "rapier_mt")]
    [InlineData(8, "rapier_mt")]
    public void LibraryNameFor_OneThreadIsRapier_MoreIsRapierMt(int threads, string name)
    {
        Assert.Equal(name, RapierNative.LibraryNameFor(threads));
    }

    [Fact]
    public void Dllmap_LoadsBothBuilds_AndHasNoLineForTheBoundName()
    {
        string? single = RapierNative.FileOf(RapierNative.LoadFromDllmap("rapier"));
        string? mt = RapierNative.FileOf(RapierNative.LoadFromDllmap("rapier_mt"));
        Assert.NotNull(single);
        Assert.NotNull(mt);
        Assert.Equal(ShippedFileName(mt: false), Path.GetFileName(single));
        Assert.Equal(ShippedFileName(mt: true), Path.GetFileName(mt));
        // The DllImports' name must be one no other resolver can map, or it could bind them to the single-threaded
        // build after TryLoad chose rapier_mt.
        Assert.Equal(IntPtr.Zero, RapierNative.LoadFromDllmap(RapierNative.Lib));
    }

    [Fact]
    public void ProcessDllConfiguration_WithNoLine_ReturnsNothing()
    {
        // OpenSim's Util.InstallNativeDllFallback asks it about RapierNative.Lib with every other line removed. It must
        // say "nothing" quietly, not throw (the fallback would then log and retry five times for every DllImport).
        DllmapConfigHelper.configuration config = new DllmapConfigHelper.configuration();
        config.maps = new List<DllmapConfigHelper.dllmap>();
        Assert.True(string.IsNullOrEmpty(DllmapConfigHelper.ProcessDllConfiguration(config)));
    }

    [Fact]
    public void LoadBuild_OneThread_LoadsTheBuildWithoutParallel()
    {
        Assert.True(RapierNative.LoadBuild(1, RapierNative.LoadFromDllmap, out IntPtr handle, out string name,
                        out string? warning, out string? error), error);
        Assert.Equal("rapier", name);
        Assert.Null(warning);
        Assert.False(IsParallel(handle));
    }

    [Fact]
    public void LoadBuild_TwoThreads_LoadsTheParallelBuild()
    {
        Assert.True(RapierNative.LoadBuild(2, RapierNative.LoadFromDllmap, out IntPtr handle, out string name,
                        out string? warning, out string? error), error);
        Assert.Equal("rapier_mt", name);
        Assert.Null(warning);
        Assert.True(IsParallel(handle));
    }

    [Fact]
    public void LoadBuild_MissingRapierMt_FallsBackToRapierWithAWarning()
    {
        Func<string, IntPtr> load = name => name == "rapier_mt" ? LoadMissing() : RapierNative.LoadFromDllmap(name);
        Assert.True(RapierNative.LoadBuild(2, load, out IntPtr handle, out string name,
                        out string? warning, out string? error), error);
        Assert.Equal("rapier", name);
        Assert.NotNull(warning);
        Assert.Contains("1 thread", warning);
        Assert.False(IsParallel(handle));
    }

    [Fact]
    public void LoadBuild_NoRapier_Fails()
    {
        Assert.False(RapierNative.LoadBuild(1, _ => IntPtr.Zero, out _, out _, out _, out string? error));
        Assert.Contains("no dllmap line", error);
    }

    [Fact]
    public void TryLoad_AnotherThreadCountLater_KeepsTheLoadedBuildAndWarns()
    {
        Assert.True(RapierNative.TryLoad(1, out string? error, out _), error);
        bool parallel = RapierNative.r3BuildFeatures().parallel != 0;
        int same = parallel ? 2 : 1, other = parallel ? 1 : 2;

        Assert.True(RapierNative.TryLoad(same, out error, out string? warning), error);
        Assert.Null(warning);
        Assert.True(RapierNative.TryLoad(other, out error, out warning), error);
        Assert.NotNull(warning);
        Assert.Contains("only once", warning);
        // Still the same library.
        Assert.Equal(parallel, RapierNative.r3BuildFeatures().parallel != 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void World_GetsAPoolOnlyFromTheParallelBuild(int threads)
    {
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "RapierNumThreads", threads.ToString() } });
        try
        {
            IntPtr world = ((BSAPIRapier.BulletWorldRapier)scene.World).World;
            bool parallel = RapierNative.r3BuildFeatures().parallel != 0;
            // The single-threaded build reports 1 and has no pool. The parallel one has a pool of the asked size,
            // 1 included: without a pool its parallel parts would run on rayon's global pool.
            Assert.Equal(parallel ? threads : 1, (int)RapierNative.r3NumThreads(world));
            Assert.Equal(parallel, scene.PE.BulletEngineVersion.EndsWith("+parallel"));
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Scene_AsksTheLoaderForItsRapierNumThreads(int threads)
    {
        // The library is already loaded for this run, so TryLoad warns exactly when the scene's thread count needs the
        // other build. That warning shows the count BSScene passed: a constant would warn for the wrong counts.
        bool loadedParallel;
        Assert.True(RapierNative.TryLoad(1, out string? error, out _), error);
        loadedParallel = RapierNative.r3BuildFeatures().parallel != 0;

        MemoryAppender appender = new MemoryAppender();
        Hierarchy repository = (Hierarchy)log4net.LogManager.GetRepository(typeof(BSScene).Assembly);
        repository.Root.AddAppender(appender);
        Level? previousLevel = repository.Root.Level;
        repository.Root.Level = Level.All;
        repository.Configured = true;
        BSScene? scene = null;
        try
        {
            scene = RapierTestScene.Create(new Dictionary<string, string> { { "RapierNumThreads", threads.ToString() } });
            LoggingEvent[] warnings = appender.GetEvents()
                .Where(e => e.Level == Level.Warn && e.RenderedMessage.Contains("loaded only once")).ToArray();
            Assert.Equal(loadedParallel != (threads > 1), warnings.Length > 0);
            Assert.All(warnings, e => Assert.Contains($"its {threads} thread(s)", e.RenderedMessage));
        }
        finally
        {
            scene?.Dispose();
            repository.Root.Level = previousLevel;
            repository.Root.RemoveAppender(appender);
        }
    }
}
