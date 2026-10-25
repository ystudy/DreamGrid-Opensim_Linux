using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class MariaDbBinariesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-bin-" + Guid.NewGuid().ToString("N"));

    public MariaDbBinariesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "");
        return p;
    }

    [Fact]
    public void Locate_reports_every_searched_path_when_missing()
    {
        var ex = Assert.Throws<MariaDbNotFoundException>(() => MariaDbBinaries.Locate(_dir, isWindows: false));
        Assert.Contains(Path.Combine(_dir, "mariadbd"), ex.Message);
        Assert.Contains(Path.Combine(_dir, "mysqld"), ex.Message);
        Assert.Contains(Path.Combine(_dir, "mariadb-install-db"), ex.Message);
        Assert.Contains(Path.Combine(_dir, "mysql_install_db"), ex.Message);
    }

    [Fact]
    public void Locate_uses_exe_suffix_on_windows_and_finds_admin()
    {
        var server = Touch("mariadbd.exe");
        var install = Touch("mariadb-install-db.exe");
        var admin = Touch("mariadb-admin.exe");
        var bin = MariaDbBinaries.Locate(_dir, isWindows: true);
        Assert.Equal(server, bin.Server);
        Assert.Equal(install, bin.InstallDb);
        Assert.Equal(admin, bin.Admin);
    }

    [Fact]
    public void Locate_falls_back_to_mysql_names_and_admin_is_optional()
    {
        var server = Touch("mysqld");
        var install = Touch("mysql_install_db");
        var bin = MariaDbBinaries.Locate(_dir, isWindows: false);
        Assert.Equal(server, bin.Server);
        Assert.Equal(install, bin.InstallDb);
        Assert.Null(bin.Admin);
    }

    [Fact]
    public void Locate_prefers_mariadb_names()
    {
        Touch("mysqld");
        Touch("mysql_install_db");
        var server = Touch("mariadbd");
        var install = Touch("mariadb-install-db");
        var bin = MariaDbBinaries.Locate(_dir, isWindows: false);
        Assert.Equal(server, bin.Server);
        Assert.Equal(install, bin.InstallDb);
    }

    private static LauncherSettings Settings() => new()
    {
        Db = new DatabaseSecrets("r", "b", "o"),
        Console = new ConsoleSecrets("u", "p"),
        MachineId = "m",
        MariaDbPort = 4406,
    };

    [Fact]
    public void StartInfo_has_the_required_arguments()
    {
        var data = Path.Combine(_dir, "data");
        var paths = new LauncherPaths(data, "/opensim", null);
        var bin = new MariaDbBinaries("/x/mariadbd", "/x/install", null);
        var psi = new MariaDbServer(bin, paths, Settings()).StartInfo();
        Assert.Equal("/x/mariadbd", psi.FileName);
        var args = psi.ArgumentList;
        Assert.Contains("--no-defaults", args);
        Assert.Contains($"--datadir={paths.MariaDbData}", args);
        Assert.Contains("--port=4406", args);
        Assert.Contains("--bind-address=127.0.0.1", args);
        Assert.Contains("--skip-name-resolve", args);
        Assert.Contains("--character-set-server=utf8mb4", args);
        Assert.Contains("--collation-server=utf8mb4_unicode_ci", args);
        Assert.Contains($"--pid-file={Path.Combine(paths.Pids, "mariadb.pid")}", args);
        Assert.Contains($"--log-error={Path.Combine(paths.Logs, "mariadb.err")}", args);
        // A private tmpdir: mariadbd deletes every "#sql*" file in its tmpdir at startup, so a shared one (/tmp)
        // destroys the temporary tables of any other MariaDB that is running or bootstrapping there.
        Assert.Equal(Path.Combine(data, "mariadb", "tmp"), paths.MariaDbTmp);
        Assert.Contains($"--tmpdir={paths.MariaDbTmp}", args);
    }

    [Fact]
    public void SocketPath_is_inside_the_data_dir_when_short()
    {
        var p = MariaDbServer.SocketPathFor("/var/lib/dreamgrid");
        Assert.Equal("/var/lib/dreamgrid/mariadb/mariadb.sock", p);
    }

    [Fact]
    public void SocketPath_falls_back_to_tmp_when_over_the_unix_limit()
    {
        var longData = "/var/lib/" + new string('d', 120);
        var p = MariaDbServer.SocketPathFor(longData);
        Assert.StartsWith("/tmp/dg-", p);
        Assert.EndsWith(".sock", p);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(p) <= 107);
        Assert.Equal(p, MariaDbServer.SocketPathFor(longData));
        Assert.NotEqual(p, MariaDbServer.SocketPathFor(longData + "x"));
    }

    [Fact]
    public void SocketPath_counts_bytes_not_characters()
    {
        // 50 two-byte characters: short in chars, but the limit is in bytes.
        var data = "/d/" + new string('é', 50);
        var p = MariaDbServer.SocketPathFor(data);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(p) <= 107);
    }
}
