using System.Runtime.Versioning;
using DreamGrid.Launcher.Core;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class LauncherPathsTests
{
    private static string Sep(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public void Resolve_prefers_args_over_env_over_default()
    {
        var env = new Dictionary<string, string?>
        {
            ["DREAMGRID_DATA"] = "/env/data",
            ["DREAMGRID_OPENSIM"] = "/env/opensim",
            ["DREAMGRID_MARIADB_BIN"] = "/env/maria",
            ["ProgramData"] = "/pd",
        };

        var fromArgs = LauncherPaths.Resolve(
            ["--data", "/arg/data", "--opensim", "/arg/opensim", "--mariadb-bin", "/arg/maria"], env, isWindows: false);
        Assert.Equal(Path.GetFullPath("/arg/data"), fromArgs.Data);
        Assert.Equal(Path.GetFullPath("/arg/opensim"), fromArgs.OpenSimProgram);
        Assert.Equal(Path.GetFullPath("/arg/maria"), fromArgs.MariaDbBin);

        var fromEnv = LauncherPaths.Resolve([], env, isWindows: false);
        Assert.Equal(Path.GetFullPath("/env/data"), fromEnv.Data);
        Assert.Equal(Path.GetFullPath("/env/opensim"), fromEnv.OpenSimProgram);
        Assert.Equal(Path.GetFullPath("/env/maria"), fromEnv.MariaDbBin);

        var defaults = LauncherPaths.Resolve([], new Dictionary<string, string?> { ["DREAMGRID_OPENSIM"] = "/o" }, isWindows: false);
        Assert.Equal(Path.GetFullPath("/var/lib/dreamgrid"), defaults.Data);
        Assert.Null(defaults.MariaDbBin);
    }

    [Theory]
    [InlineData("--data")]
    [InlineData("--opensim")]
    [InlineData("--mariadb-bin")]
    public void Resolve_trailing_flag_without_value_throws(string flag)
    {
        var env = new Dictionary<string, string?> { ["DREAMGRID_OPENSIM"] = "/o" };
        var e = Assert.Throws<ArgumentException>(() => LauncherPaths.Resolve(["--other", flag], env, isWindows: false));
        Assert.Contains(flag, e.Message);
        Assert.Contains("needs a value", e.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_windows_without_ProgramData_uses_C_ProgramData(string? programData)
    {
        var env = new Dictionary<string, string?> { ["DREAMGRID_OPENSIM"] = "/o" };
        if (programData is not null) env["ProgramData"] = programData;
        Assert.Equal(Path.Combine(@"C:\ProgramData", "DreamGrid"),LauncherPaths.ResolveData([], env, isWindows: true));
    }

    [Fact]
    public void Resolve_throws_naming_opensim_when_unset()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LauncherPaths.Resolve([], new Dictionary<string, string?>(), isWindows: false));
        Assert.Contains("--opensim", ex.Message);
        Assert.Contains("DREAMGRID_OPENSIM", ex.Message);
    }

    [Fact]
    public void Default_data_dir_is_ProgramData_on_windows_and_var_lib_on_linux()
    {
        var env = new Dictionary<string, string?>
        {
            ["ProgramData"] = Path.Combine(Path.GetTempPath(), "PD"),
            ["DREAMGRID_OPENSIM"] = "/o",
        };

        Assert.Equal(Path.Combine(env["ProgramData"]!, "DreamGrid"), LauncherPaths.Resolve([], env, isWindows: true).Data);
        Assert.Equal(Path.GetFullPath("/var/lib/dreamgrid"), LauncherPaths.Resolve([], env, isWindows: false).Data);
    }

    [Fact]
    public void RegionDir_and_RegionDefinitionDir_follow_the_layout()
    {
        var p = new LauncherPaths("/d", "/o", null);
        Assert.EndsWith(Sep("state/regions/Welcome"), p.RegionDir("Welcome"));
        Assert.EndsWith(Sep("state/regions/Welcome/Region"), p.RegionDefinitionDir("Welcome"));
    }

    [Fact]
    public void EnsureCreated_handles_spaces_and_non_ascii()
    {
        var data = Path.Combine(Path.GetTempPath(), $"Dréam Grid {Guid.NewGuid():N}");
        try
        {
            var p = new LauncherPaths(data, "/o", null);
            p.EnsureCreated();

            string[] dirs =
            [
                p.Data, p.MariaDbData, p.MariaDbTmp, p.RuntimeOpenSim, p.RegionsRoot, p.AssetCache, p.FsAssetsData,
                p.FsAssetsSpool, p.ScriptEngines, p.Logs, p.Pids, p.Oars, p.OarsIncoming, p.OarsSaving,
            ];
            foreach (var d in dirs)
                Assert.True(Directory.Exists(d), d);
            Assert.Equal(Path.Combine(data, "settings.json"), p.SettingsFile);
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public void Resolve_makes_relative_paths_absolute()
    {
        // A relative --data would otherwise follow the working directory of whichever child uses it.
        var env = new Dictionary<string, string?> { ["DREAMGRID_DATA"] = "rel-env-data", ["DREAMGRID_OPENSIM"] = "rel-env-opensim" };

        var fromArgs = LauncherPaths.Resolve(["--data", "rel-data", "--opensim", "rel-opensim", "--mariadb-bin", "rel-maria"], env, OperatingSystem.IsWindows());
        Assert.Equal(Path.GetFullPath("rel-data"), fromArgs.Data);
        Assert.Equal(Path.GetFullPath("rel-opensim"), fromArgs.OpenSimProgram);
        Assert.Equal(Path.GetFullPath("rel-maria"), fromArgs.MariaDbBin);

        var fromEnv = LauncherPaths.Resolve([], env, OperatingSystem.IsWindows());
        Assert.Equal(Path.GetFullPath("rel-env-data"), fromEnv.Data);
        Assert.Equal(Path.GetFullPath("rel-env-opensim"), fromEnv.OpenSimProgram);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void EnsureCreated_makes_the_data_dir_owner_only_on_linux()
    {
        // The generated INIs under <data> carry the database passwords, ConsolePass and the MachineID.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var data = Path.Combine(Path.GetTempPath(), "dg-mode-" + Guid.NewGuid().ToString("N"));
        var existing = Path.Combine(Path.GetTempPath(), "dg-mode-" + Guid.NewGuid().ToString("N"));
        try
        {
            new LauncherPaths(data, "/o", null).EnsureCreated();
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(data));

            Directory.CreateDirectory(existing);
            File.SetUnixFileMode(existing, (UnixFileMode)0b111_101_101); // 0755, as an earlier version left it
            new LauncherPaths(existing, "/o", null).EnsureCreated();
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(existing));
        }
        finally
        {
            foreach (var d in new[] { data, existing })
                if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_data_dir_is_owner_system_admins_only()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var dir = Path.Combine(Path.GetTempPath(), "dg-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            var expected = new[] { me, "S-1-5-18", "S-1-5-32-544" }.Distinct().Order().ToArray();

            LauncherPaths.CreateOwnerOnly(dir);
            LauncherPaths.CreateOwnerOnly(dir); // idempotent

            var security = new DirectoryInfo(dir).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(expected, Sids(security));

            var file = Path.Combine(dir, "secret.txt");
            File.WriteAllText(file, "x");
            Assert.Equal(expected, Sids(new FileInfo(file).GetAccessControl()));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        static string[] Sids(System.Security.AccessControl.FileSystemSecurity s) =>
            s.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                .Select(r => r.IdentityReference.Value).Distinct().Order().ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static string[] AclSids(System.Security.AccessControl.FileSystemSecurity s, bool inherited = true) =>
        s.GetAccessRules(true, inherited, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .Select(r => r.IdentityReference.Value).Distinct().Order().ToArray();

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Rewrite_replaces_inherited_and_extra_entries()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var dir = Path.Combine(Path.GetTempPath(), "dg-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var users = new System.Security.Principal.SecurityIdentifier("S-1-5-32-545");
            var localService = new System.Security.Principal.SecurityIdentifier("S-1-5-19");
            var info = Directory.CreateDirectory(dir);
            var seed = info.GetAccessControl();
            seed.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users,
                System.Security.AccessControl.FileSystemRights.ReadAndExecute,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(seed);
            var child = Path.Combine(dir, "old.txt");
            File.WriteAllText(child, "x");
            Assert.Contains(users.Value, AclSids(new FileInfo(child).GetAccessControl()));

            LauncherPaths.ApplyOwnerOnlyAcl(dir, [localService]);

            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            var expected = new[] { me, "S-1-5-18", "S-1-5-32-544", "S-1-5-19" }.Distinct().Order().ToArray();
            var after = new DirectoryInfo(dir).GetAccessControl();
            Assert.True(after.AreAccessRulesProtected);
            Assert.Equal(expected, AclSids(after));
            Assert.Equal(expected, AclSids(new FileInfo(child).GetAccessControl()));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Extra_service_sid_is_kept_on_rerun()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var dir = Path.Combine(Path.GetTempPath(), "dg-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var localService = new System.Security.Principal.SecurityIdentifier("S-1-5-19");
            Directory.CreateDirectory(dir);
            LauncherPaths.ApplyOwnerOnlyAcl(dir, [localService]);
            var first = new DirectoryInfo(dir).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);

            LauncherPaths.ApplyOwnerOnlyAcl(dir, [localService]);
            var second = new DirectoryInfo(dir).GetAccessControl();
            Assert.Equal(first, second.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access));
            Assert.Contains("S-1-5-19", AclSids(second));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
