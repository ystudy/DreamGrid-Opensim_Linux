using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using Xunit;

// BSScene's parameters (BSParam) and the one loaded Rapier library are process-wide, so tests run one at a time.
[assembly: Xunit.CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// Builds a BSScene that runs on the Rapier backend, the way BulletS/Tests/BulletSimTestsUtil.cs builds one for
/// Bullet, and steps it the way the simulator's heartbeat does.
/// </summary>
public static class RapierTestScene
{
    /// <summary>The simulator's physics frame: about 11 per second.</summary>
    public const float FrameSeconds = 0.0909f;

    public static BSScene Create(Dictionary<string, string>? overrides = null, uint regionSize = 256)
    {
        IConfigSource openSimIni = new IniConfigSource();
        IConfig startupConfig = openSimIni.AddConfig("Startup");
        startupConfig.Set("physics", "BulletSim");
        startupConfig.Set("meshing", "Meshmerizer");
        startupConfig.Set("cacheSculptMaps", "false");

        IConfig bulletSimConfig = openSimIni.AddConfig("BulletSim");
        bulletSimConfig.Set("BulletEngine", "Rapier");
        bulletSimConfig.Set("MeshSculptedPrim", "false");
        bulletSimConfig.Set("ForceSimplePrimMeshing", "true");
        if (overrides != null)
        {
            foreach (KeyValuePair<string, string> kvp in overrides)
                bulletSimConfig.Set(kvp.Key, kvp.Value);
        }

        RegionInfo info = new RegionInfo();
        info.RegionName = "RapierTestRegion";
        info.RegionSizeX = info.RegionSizeY = info.RegionSizeZ = regionSize;
        OpenSim.Region.Framework.Scenes.Scene scene = new OpenSim.Region.Framework.Scenes.Scene(info);

        IMesher mesher = new OpenSim.Region.PhysicsModule.Meshing.Meshmerizer();
        INonSharedRegionModule mod = (INonSharedRegionModule)mesher;
        mod.Initialise(openSimIni);
        mod.AddRegion(scene);
        mod.RegionLoaded(scene);

        BSScene physicsScene = new BSScene();
        mod = physicsScene;
        mod.Initialise(openSimIni);
        mod.AddRegion(scene);
        mod.RegionLoaded(scene);
        return physicsScene;
    }

    /// <summary>Calls Simulate at the simulator's frame length until at least this many seconds have gone by.</summary>
    public static void Step(BSScene s, float seconds)
    {
        float done = 0f;
        while (done < seconds - 0.0001f)
        {
            s.Simulate(FrameSeconds);
            done += FrameSeconds;
        }
    }
}

/// <summary>
/// Collects what the BulletS code logs at error level while a test runs. BSScene catches an exception thrown in a taint
/// (the creation of a prim, say) and logs it, so a test that only looks at positions would not see an engine member
/// that threw.
/// </summary>
public sealed class ErrorLog : IDisposable
{
    private readonly log4net.Appender.MemoryAppender _appender = new log4net.Appender.MemoryAppender();
    private readonly log4net.Repository.Hierarchy.Hierarchy _repository;
    private readonly log4net.Core.Level? _previousLevel;

    public ErrorLog()
    {
        _repository = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository(typeof(BSScene).Assembly);
        _previousLevel = _repository.Root.Level;
        _repository.Root.AddAppender(_appender);
        _repository.Root.Level = log4net.Core.Level.All;
        _repository.Configured = true;
    }

    /// <summary>Every error-level message so far, with its exception when it has one.</summary>
    public List<string> Errors
    {
        get
        {
            return _appender.GetEvents()
                .Where(e => e.Level >= log4net.Core.Level.Error)
                .Select(e => e.RenderedMessage + (e.ExceptionObject != null ? Environment.NewLine + e.ExceptionObject : ""))
                .ToList();
        }
    }

    public void Dispose()
    {
        _repository.Root.RemoveAppender(_appender);
        _repository.Root.Level = _previousLevel;
    }
}
