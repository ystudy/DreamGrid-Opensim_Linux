namespace DreamGrid.Launcher.Core.Database;

/// <summary>Raised when the MariaDB server or install program cannot be found; names every path searched.</summary>
public sealed class MariaDbNotFoundException(string message) : Exception(message);

/// <summary>The MariaDB programs the launcher drives.</summary>
public sealed record MariaDbBinaries(string Server, string InstallDb, string? Admin)
{
    /// <summary>
    /// Finds the server and install programs in <paramref name="binDir"/>; with no directory,
    /// searches <c>/usr/sbin</c> and <c>/usr/bin</c> on Linux.
    /// </summary>
    public static MariaDbBinaries Locate(string? binDir, bool isWindows)
    {
        // Debian puts mariadbd in /usr/sbin but mariadb-install-db in /usr/bin, so the system
        // directories are always searched together.
        var systemDirs = new[] { "/usr/sbin", "/usr/bin" };
        var dirs = string.IsNullOrWhiteSpace(binDir)
            ? isWindows ? Array.Empty<string>() : systemDirs
            : !isWindows && systemDirs.Contains(binDir.TrimEnd('/')) ? systemDirs : new[] { binDir };
        var ext = isWindows ? ".exe" : "";

        string? Find(string[] names, List<string> searched)
        {
            foreach (var name in names)
                foreach (var dir in dirs)
                {
                    var path = Path.Combine(dir, name + ext);
                    searched.Add(path);
                    if (File.Exists(path)) return path;
                }
            return null;
        }

        var serverSearched = new List<string>();
        var installSearched = new List<string>();
        var server = Find(["mariadbd", "mysqld"], serverSearched);
        var install = Find(["mariadb-install-db", "mysql_install_db"], installSearched);
        if (server is null || install is null)
        {
            var lines = new List<string>();
            if (server is null) lines.Add("MariaDB server not found; searched: " + Describe(serverSearched));
            if (install is null) lines.Add("MariaDB install program not found; searched: " + Describe(installSearched));
            throw new MariaDbNotFoundException(string.Join(Environment.NewLine, lines)
                + Environment.NewLine + "Pass --mariadb-bin <dir> or set DREAMGRID_MARIADB_BIN.");
        }

        return new MariaDbBinaries(server, install, Find(["mariadb-admin", "mysqladmin"], new List<string>()));
    }

    private static string Describe(List<string> searched) =>
        searched.Count == 0 ? "(no directory to search)" : string.Join(", ", searched);
}
