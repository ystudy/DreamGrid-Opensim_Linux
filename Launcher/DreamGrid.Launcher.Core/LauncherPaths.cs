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
    /// <summary>The website's ASP.NET Core data-protection keys (they sign the sign-in and antiforgery cookies).</summary>
    public string DataProtectionKeys => Path.Combine(Data, "keys");

    public string RegionDir(string instance) => Path.Combine(RegionsRoot, instance);
    public string RegionDefinitionDir(string instance) => Path.Combine(RegionDir(instance), "Region");

    /// <summary>Picks a flag's value from the arguments, else the environment variable; a flag with nothing after it throws.</summary>
    private static string? Pick(string[] args, IDictionary<string, string?> env, string arg, string envKey)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != arg) continue;
            if (i == args.Length - 1)
                throw new ArgumentException($"{arg} needs a value.");
            if (!string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1];
        }
        return env.TryGetValue(envKey, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    /// <summary>Resolves the data directory: argument, then environment, then OS default.</summary>
    public static string ResolveData(string[] args, IDictionary<string, string?> env, bool isWindows)
    {
        var data = Pick(args, env, "--data", "DREAMGRID_DATA");
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
        return data;
    }

    /// <summary>Resolves paths: argument, then environment, then OS default.</summary>
    public static LauncherPaths Resolve(string[] args, IDictionary<string, string?> env, bool isWindows)
    {
        var data = ResolveData(args, env, isWindows);

        var opensim = Pick(args, env, "--opensim", "DREAMGRID_OPENSIM")
            ?? throw new ArgumentException("The OpenSim program directory is required: pass --opensim <dir> or set DREAMGRID_OPENSIM.");

        // Children run with other working directories, so every path is made absolute here.
        var mariaDbBin = Pick(args, env, "--mariadb-bin", "DREAMGRID_MARIADB_BIN");
        return new LauncherPaths(Path.GetFullPath(data), Path.GetFullPath(opensim),
            mariaDbBin is null ? null : Path.GetFullPath(mariaDbBin));
    }

    /// <summary>Creates every directory in the layout; on Unix <see cref="Data"/> is owner-only (it holds secrets).</summary>
    public void EnsureCreated()
    {
        CreateOwnerOnly(Data);
        foreach (var dir in new[]
        {
            Data, MariaDbData, MariaDbTmp, RuntimeOpenSim, RegionsRoot, AssetCache, FsAssetsData, FsAssetsSpool,
            ScriptEngines, Logs, Pids, DataProtectionKeys,
        })
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Creates <paramref name="dir"/> mode 0700 on Unix (on Windows: a protected ACL for the current user, SYSTEM and
    /// Administrators only), and narrows an existing one the same way: the generated INIs and
    /// settings.json carry the database passwords, the console password and the MachineID.
    /// </summary>
    public static void CreateOwnerOnly(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            RestrictToOwnerSystemAdmins(dir);
            return;
        }
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, OwnerOnly);
        if (File.GetUnixFileMode(dir) != OwnerOnly) File.SetUnixFileMode(dir, OwnerOnly);
    }

    /// <summary>Replaces the directory's DACL with a protected one (no inherited entries): FullControl, inherited by children, for the current user, SYSTEM and Administrators.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictToOwnerSystemAdmins(string dir)
    {
        var sids = new[]
        {
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
        }.Distinct().ToArray();
        const System.Security.AccessControl.InheritanceFlags Inherit =
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;

        var info = new DirectoryInfo(dir);
        var current = info.GetAccessControl();
        var rules = current.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        // Already right: skip the write (this runs on every settings save).
        if (current.AreAccessRulesProtected && rules.Count == sids.Length && rules.All(r =>
                !r.IsInherited && r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                && r.FileSystemRights == System.Security.AccessControl.FileSystemRights.FullControl
                && r.InheritanceFlags == Inherit && r.PropagationFlags == System.Security.AccessControl.PropagationFlags.None
                && sids.Contains(r.IdentityReference)))
            return;

        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in sids)
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                sid, System.Security.AccessControl.FileSystemRights.FullControl, Inherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
