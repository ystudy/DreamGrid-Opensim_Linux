using System.Text.Json;
using DreamGrid.Launcher.Core.Config;

namespace DreamGrid.Launcher.Core.Settings;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, RespectNullableAnnotations = true };

    private int _swept;

    public LauncherSettings LoadOrCreate()
    {
        // A .tmp found at the first load is a save that never reached the rename (this process has not saved yet);
        // the real file is the truth. Later loads leave it alone: it may be a save in flight.
        if (Interlocked.Exchange(ref _swept, 1) == 0)
            try { File.Delete(path + ".tmp"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        if (!File.Exists(path))
        {
            var fresh = NewSettings();
            Save(fresh);
            return fresh;
        }

        return Read();
    }

    /// <summary>The saved settings, or null when the file does not exist. Creates nothing.</summary>
    public LauncherSettings? TryLoad() => File.Exists(path) ? Read() : null;

    private LauncherSettings Read()
    {
        try
        {
            // ReadWrite | Delete sharing keeps the read from blocking writers longer than it must (see AtomicFile).
            string json;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();
            var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, Options)
                ?? throw new JsonException("settings file contains null");
            Validate(loaded);
            if (loaded.SchemaVersion >= LauncherSettings.CurrentSchema) return loaded;
            var migrated = Migrate(loaded);
            Save(migrated);
            return migrated;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new SettingsCorruptException(path, e);
        }
    }

    /// <summary>
    /// The values a hand edit could make unsafe are checked as the Security page checks them: a supervisor level of 0 would make
    /// every guest a supervisor. Settings that fail refuse to load, like a file that doesn't parse.
    /// </summary>
    private void Validate(LauncherSettings s)
    {
        var security = SettingsValidator.Security(s.Security);
        if (!security.Ok)
            throw new SettingsCorruptException(path,
                security.Errors.Select(e => $"security.{JsonNamingPolicy.CamelCase.ConvertName(e.Key)}: {e.Value}"));
    }

    /// <summary>
    /// Migrates to the current schema in one load. Schema 1 to 2: the single region becomes one program and one region with the same folder, ports and UUID, so a
    /// running region is adopted with no change to its config.
    /// </summary>
    private static LauncherSettings Migrate(LauncherSettings s)
    {
        if (s.SchemaVersion < 2) s = MigrateV1(s);
        // Schema 2 to 3: "ubODE" was the default then; now null means the grid default, which starts as ubODE.
        // Schema 2's "BulletSim" ran with the prototype's UseSeparatePhysicsThread=true, which is "BulletSimThreaded" now.
        var programs = s.Programs.Select(p => p.Physics switch
        {
            "ubODE" => p with { Physics = null },
            "BulletSim" => p with { Physics = "BulletSimThreaded" },
            _ => p,
        }).ToList();
        s = s with { Programs = programs };
        // Schema 3 to 4: a finished install gets the plan its address implies, with hypergrid on only for a public one.
        // An unfinished setup keeps both at their defaults and chooses at first use.
        if (s.SchemaVersion < 4 && s.Setup.Finished)
        {
            var plan = HostAddress.IsPublic(s.Hostname) ? OwnerPlan.SimplePublic : OwnerPlan.Local;
            s = s with { Plan = plan, Hypergrid = plan == OwnerPlan.SimplePublic };
        }
        // Schema 4 to 5: security settings arrive with their defaults; only the version changes.
        return s with { SchemaVersion = LauncherSettings.CurrentSchema };
    }

    private static LauncherSettings MigrateV1(LauncherSettings s)
    {
        var programs = new List<ProgramSettings>();
        var regions = new List<RegionSettings>();
        if (s.LegacyRegion is { } r)
        {
            programs.Add(new ProgramSettings(r.Instance, s.RegionPort, s.RegionConsolePort, r.Physics));
            regions.Add(new RegionSettings(r.Instance, r.Name, r.Uuid, r.X, r.Y, r.Content, 1, s.RegionPort));
        }
        return s with { SchemaVersion = 2, Programs = programs, Regions = regions, LegacyRegion = null };
    }

    public void Save(LauncherSettings s)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) LauncherPaths.CreateOwnerOnly(dir); // <data>: it holds the secrets
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(s, Options));
    }

    private static LauncherSettings NewSettings() => new()
    {
        Db = new DatabaseSecrets(Secrets.NewPassword(), Secrets.NewPassword(), Secrets.NewPassword()),
        Console = new ConsoleSecrets("dg" + Secrets.NewPassword(8), Secrets.NewPassword()),
        MachineId = Secrets.NewMachineId(),
        SetupToken = Secrets.NewPassword(32),
    };
}
