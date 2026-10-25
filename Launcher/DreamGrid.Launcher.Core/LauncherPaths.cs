namespace DreamGrid.Launcher.Core;

/// <summary>The launcher's data-directory layout and program locations.</summary>
public sealed record LauncherPaths(string Data, string OpenSimProgram, string? MariaDbBin)
{
    public string SettingsFile => Path.Combine(Data, "settings.json");
    public string MariaDbData => Path.Combine(Data, "mariadb", "data");
    /// <summary>The private MariaDB's tmpdir (mariadbd empties its tmpdir of "#sql*" files at startup, so it is never shared).</summary>
    public string MariaDbTmp => Path.Combine(Data, "mariadb", "tmp");
    public string RuntimeOpenSim => Path.Combine(Data, "runtime", "opensim");
    public string RegionsRoot => Path.Combine(Data, "state", "regions");
    public string AssetCache => Path.Combine(Data, "state", "assetcache");
    public string FsAssetsData => Path.Combine(Data, "state", "fsassets", "data");
    public string FsAssetsSpool => Path.Combine(Data, "state", "fsassets", "tmp");
    public string ScriptEngines => Path.Combine(Data, "state", "scriptengines");
    public string Logs => Path.Combine(Data, "state", "logs");
    public string Pids => Path.Combine(Data, "state", "pids");

    public string RegionDir(string instance) => Path.Combine(RegionsRoot, instance);
    public string RegionDefinitionDir(string instance) => Path.Combine(RegionDir(instance), "Region");

    /// <summary>Resolves paths: argument, then environment, then OS default.</summary>
    public static LauncherPaths Resolve(string[] args, IDictionary<string, string?> env, bool isWindows)
    {
        string? Pick(string arg, string envKey)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == arg && !string.IsNullOrWhiteSpace(args[i + 1]))
                    return args[i + 1];
            return env.TryGetValue(envKey, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        }

        var data = Pick("--data", "DREAMGRID_DATA");
        if (data is null)
        {
            if (isWindows)
            {
                env.TryGetValue("ProgramData", out var pd);
                data = Path.Combine(string.IsNullOrWhiteSpace(pd) ? @"C:\ProgramData" : pd, "DreamGrid");
            }
            else
            {
                data = "/var/lib/dreamgrid";
            }
        }

        var opensim = Pick("--opensim", "DREAMGRID_OPENSIM")
            ?? throw new ArgumentException("The OpenSim program directory is required: pass --opensim <dir> or set DREAMGRID_OPENSIM.");

        return new LauncherPaths(data, opensim, Pick("--mariadb-bin", "DREAMGRID_MARIADB_BIN"));
    }

    /// <summary>Creates every directory in the layout.</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[]
        {
            Data, MariaDbData, MariaDbTmp, RuntimeOpenSim, RegionsRoot, AssetCache, FsAssetsData, FsAssetsSpool,
            ScriptEngines, Logs, Pids,
        })
            Directory.CreateDirectory(dir);
    }
}
