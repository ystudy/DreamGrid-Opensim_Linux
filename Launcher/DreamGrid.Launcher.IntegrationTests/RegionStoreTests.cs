using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>
/// <see cref="RegionStore"/> on a real MariaDB. Each test makes fresh robust and opensim databases holding only the
/// tables it touches, with OpenSim's own table and column names (RegionStore.migrations, EstateStore.migrations and
/// GridStore.migrations); the other columns are left out.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class RegionStoreTests : IAsyncDisposable
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-regionstore-" + Guid.NewGuid().ToString("N"));
    private Process? _server;
    private MariaDbServer? _maria;
    private LauncherSettings _s = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts a MariaDB of its own with empty robust and opensim databases; skips without MariaDB binaries.</summary>
    private async Task StartAsync()
    {
        var bin = IntegrationEnvironment.RequireMariaDbBin();
        var paths = new LauncherPaths(_root, _root, bin);
        paths.EnsureCreated();
        _s = new LauncherSettings
        {
            Db = new DatabaseSecrets(Password(), Password(), Password()),
            Console = new ConsoleSecrets("u", Password()),
            MachineId = "test",
            MariaDbPort = FreePort(),
        };
        _maria = new MariaDbServer(MariaDbBinaries.Locate(bin, OperatingSystem.IsWindows()), paths, _s);
        await _maria.InitializeAsync(Ct);
        var psi = _maria.StartInfo();
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        _server = Process.Start(psi)!;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await _maria.PingAsync(Ct))
        {
            Assert.False(_server.HasExited, "mariadbd exited while waiting for it to answer.");
            Assert.True(DateTime.UtcNow < deadline, "MariaDB did not answer within 60 s.");
            await Task.Delay(500, Ct);
        }
        await Sql("CREATE DATABASE `robust`; CREATE DATABASE `opensim`;");
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            try { if (_maria is not null && !_server.HasExited) await _maria.ShutdownAsync(CancellationToken.None); } catch { }
            if (!_server.WaitForExit(30_000)) _server.Kill(true);
            _server.Dispose();
        }
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); return; }
            catch { Thread.Sleep(500); }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Password() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private string Root => new MySqlConnectionStringBuilder
    {
        Server = "127.0.0.1", Port = (uint)_s.MariaDbPort, UserID = "root", Password = _s.Db.RootPassword,
        SslMode = MySqlSslMode.Disabled, AllowUserVariables = true,
    }.ConnectionString;

    private async Task Sql(string sql)
    {
        await using var conn = new MySqlConnection(Root);
        await conn.OpenAsync(Ct);
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> Count(string table, string column, Guid id)
    {
        await using var conn = new MySqlConnection(Root);
        await conn.OpenAsync(Ct);
        await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {table} WHERE `{column}` = @id", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    private async Task<long> Scalar(string sql)
    {
        await using var conn = new MySqlConnection(Root);
        await conn.OpenAsync(Ct);
        await using var cmd = new MySqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    // The tables, with OpenSim's names for the columns the store reads.
    private const string Prims = "CREATE TABLE `opensim`.`prims` (`UUID` char(36) NOT NULL DEFAULT '', `RegionUUID` char(36) DEFAULT NULL, `Name` varchar(255) DEFAULT NULL, PRIMARY KEY (`UUID`), KEY `prims_regionuuid` (`RegionUUID`)) ENGINE=InnoDB DEFAULT CHARSET=latin1;";
    private const string PrimShapes = "CREATE TABLE `opensim`.`primshapes` (`UUID` char(36) NOT NULL DEFAULT '', `Texture` longblob, PRIMARY KEY (`UUID`)) ENGINE=InnoDB DEFAULT CHARSET=latin1;";
    private const string PrimItems = "CREATE TABLE `opensim`.`primitems` (`itemID` char(36) NOT NULL DEFAULT '', `primID` char(36) DEFAULT NULL, `name` varchar(255) DEFAULT NULL, PRIMARY KEY (`itemID`), KEY `primitems_primid` (`primID`)) ENGINE=InnoDB DEFAULT CHARSET=latin1;";
    private const string Terrain = "CREATE TABLE `opensim`.`terrain` (`RegionUUID` varchar(255) DEFAULT NULL, `Revision` int(11) DEFAULT NULL, `Heightfield` longblob) ENGINE=InnoDB DEFAULT CHARSET=latin1;";
    private const string BakedTerrain = "CREATE TABLE `opensim`.`bakedterrain` (`RegionUUID` varchar(255) DEFAULT NULL, `Revision` int(11) DEFAULT NULL, `Heightfield` longblob) ENGINE=InnoDB DEFAULT CHARSET=utf8;";
    private const string Regions = "CREATE TABLE `robust`.`regions` (`uuid` varchar(36) NOT NULL, `regionHandle` bigint(20) unsigned NOT NULL, `regionName` varchar(128) DEFAULT NULL, PRIMARY KEY (`uuid`)) ENGINE=InnoDB DEFAULT CHARSET=utf8;";

    /// <summary>Two prims (one with a shape and two items) and terrain for <paramref name="id"/>; the names are per region.</summary>
    private static string PrimsOf(Guid id, string tag) => $"""
        INSERT INTO `opensim`.`prims` (`UUID`, `RegionUUID`, `Name`) VALUES ('{tag}0000000-0000-0000-0000-00000000000a', '{id}', 'one'), ('{tag}0000000-0000-0000-0000-00000000000b', '{id}', 'two');
        INSERT INTO `opensim`.`primshapes` (`UUID`, `Texture`) VALUES ('{tag}0000000-0000-0000-0000-00000000000a', REPEAT('x', 100));
        INSERT INTO `opensim`.`primitems` (`itemID`, `primID`, `name`) VALUES ('{tag}1000000-0000-0000-0000-00000000000a', '{tag}0000000-0000-0000-0000-00000000000a', 'i1'), ('{tag}2000000-0000-0000-0000-00000000000a', '{tag}0000000-0000-0000-0000-00000000000a', 'i2');
        """;

    [Fact]
    public async Task Erase_removes_only_that_regions_rows()
    {
        await StartAsync();
        await Sql(Prims + PrimShapes + PrimItems + Terrain + BakedTerrain + Regions + """
            CREATE TABLE `opensim`.`land` (`UUID` varchar(255) NOT NULL, `RegionUUID` varchar(255) DEFAULT NULL, `Name` varchar(255) DEFAULT NULL, PRIMARY KEY (`UUID`)) ENGINE=InnoDB DEFAULT CHARSET=utf8;
            CREATE TABLE `opensim`.`landaccesslist` (`LandUUID` varchar(255) DEFAULT NULL, `AccessUUID` varchar(255) DEFAULT NULL, `Flags` int(11) DEFAULT NULL) ENGINE=InnoDB DEFAULT CHARSET=latin1;
            CREATE TABLE `opensim`.`regionban` (`regionUUID` varchar(36) NOT NULL, `bannedUUID` varchar(36) NOT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8;
            CREATE TABLE `opensim`.`regionsettings` (`regionUUID` char(36) NOT NULL, PRIMARY KEY (`regionUUID`)) ENGINE=InnoDB;
            CREATE TABLE `opensim`.`regionwindlight` (`region_id` varchar(36) NOT NULL DEFAULT '000000-0000-0000-0000-000000000000', PRIMARY KEY (`region_id`)) ENGINE=InnoDB;
            CREATE TABLE `opensim`.`spawn_points` (`RegionID` varchar(36) NOT NULL, `Yaw` float NOT NULL, KEY `RegionID` (`RegionID`)) ENGINE=InnoDB;
            CREATE TABLE `opensim`.`regionenvironment` (`region_id` varchar(36) NOT NULL, `llsd_settings` mediumtext, PRIMARY KEY (`region_id`)) ENGINE=InnoDB;
            CREATE TABLE `opensim`.`regionextra` (`RegionID` char(36) NOT NULL, `Name` varchar(32) NOT NULL, `value` text, PRIMARY KEY (`RegionID`, `Name`)) ENGINE=InnoDB;
            CREATE TABLE `opensim`.`estate_map` (`RegionID` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000', `EstateID` int(11) NOT NULL, PRIMARY KEY (`RegionID`)) ENGINE=InnoDB DEFAULT CHARSET=utf8;
            """);
        foreach (var (id, tag) in new[] { (A, "a"), (B, "b") })
            await Sql(PrimsOf(id, tag) + $"""
                INSERT INTO `opensim`.`terrain` VALUES ('{id}', 1, REPEAT('t', 50));
                INSERT INTO `opensim`.`bakedterrain` VALUES ('{id}', 1, REPEAT('t', 50));
                INSERT INTO `opensim`.`land` (`UUID`, `RegionUUID`, `Name`) VALUES ('{tag}3000000-0000-0000-0000-00000000000a', '{id}', 'parcel');
                INSERT INTO `opensim`.`landaccesslist` VALUES ('{tag}3000000-0000-0000-0000-00000000000a', '{Guid.NewGuid()}', 1);
                INSERT INTO `opensim`.`regionban` VALUES ('{id}', '{Guid.NewGuid()}');
                INSERT INTO `opensim`.`regionsettings` VALUES ('{id}');
                INSERT INTO `opensim`.`regionwindlight` VALUES ('{id}');
                INSERT INTO `opensim`.`spawn_points` VALUES ('{id}', 0);
                INSERT INTO `opensim`.`regionenvironment` VALUES ('{id}', '');
                INSERT INTO `opensim`.`regionextra` VALUES ('{id}', 'x', 'y');
                INSERT INTO `opensim`.`estate_map` VALUES ('{id}', 101);
                INSERT INTO `robust`.`regions` (`uuid`, `regionHandle`, `regionName`) VALUES ('{id}', 1, '{tag}');
                """);

        await new RegionStore(_s).EraseAsync(A, Ct);

        var byRegion = new (string Table, string Column)[]
        {
            ("`opensim`.`prims`", "RegionUUID"), ("`opensim`.`terrain`", "RegionUUID"), ("`opensim`.`bakedterrain`", "RegionUUID"),
            ("`opensim`.`land`", "RegionUUID"), ("`opensim`.`regionban`", "regionUUID"), ("`opensim`.`regionsettings`", "regionUUID"),
            ("`opensim`.`regionwindlight`", "region_id"), ("`opensim`.`spawn_points`", "RegionID"),
            ("`opensim`.`regionenvironment`", "region_id"), ("`opensim`.`regionextra`", "RegionID"),
            ("`opensim`.`estate_map`", "RegionID"), ("`robust`.`regions`", "uuid"),
        };
        foreach (var (table, column) in byRegion)
        {
            Assert.True(await Count(table, column, A) == 0, $"{table} still holds A");
            Assert.True(await Count(table, column, B) > 0, $"{table} lost B");
        }
        // The rows that hang off A's prims and parcels went with them; B's stay.
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM `opensim`.`primshapes`"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM `opensim`.`primshapes` WHERE `UUID` LIKE 'b%'"));
        Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM `opensim`.`primitems` WHERE `primID` LIKE 'b%'"));
        Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM `opensim`.`primitems`"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM `opensim`.`landaccesslist`"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM `opensim`.`landaccesslist` WHERE `LandUUID` LIKE 'b%'"));
    }

    [Fact]
    public async Task Space_estimates_bytes_per_region()
    {
        await StartAsync();
        await Sql(Prims + PrimShapes + PrimItems + Terrain + BakedTerrain);
        await Sql(PrimsOf(A, "a") + $"""
            INSERT INTO `opensim`.`terrain` VALUES ('{A}', 1, REPEAT('t', 200000));
            INSERT INTO `opensim`.`bakedterrain` VALUES ('{A}', 1, REPEAT('t', 100000));
            INSERT INTO `opensim`.`terrain` VALUES ('{B}', 1, REPEAT('t', 1000));
            ANALYZE TABLE `opensim`.`prims`, `opensim`.`primshapes`, `opensim`.`primitems`;
            """);
        var avg = new Dictionary<string, long>();
        foreach (var table in new[] { "prims", "primshapes", "primitems" })
            avg[table] = await Scalar($"SELECT AVG_ROW_LENGTH FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'opensim' AND TABLE_NAME = '{table}'");
        var none = Guid.NewGuid();

        var space = await new RegionStore(_s).SpaceAsync([A, B, none], Ct);

        Assert.Equal(2 * avg["prims"] + 1 * avg["primshapes"] + 2 * avg["primitems"] + 200000 + 100000, space[A]);
        Assert.Equal(1000, space[B]); // no prims: its terrain only
        Assert.Equal(0, space[none]);
    }

    [Fact]
    public async Task Deregister_removes_robust_row()
    {
        await StartAsync();
        await Sql(Regions + $"INSERT INTO `robust`.`regions` (`uuid`, `regionHandle`, `regionName`) VALUES ('{A}', 1, 'a'), ('{B}', 2, 'b');");

        await new RegionStore(_s).DeregisterAsync(A, Ct);

        Assert.Equal(0, await Count("`robust`.`regions`", "uuid", A));
        Assert.Equal(1, await Count("`robust`.`regions`", "uuid", B));
    }

    [Fact]
    public async Task Missing_tables_are_skipped()
    {
        // OpenSim makes its tables at its first start: before that the databases are empty.
        await StartAsync();
        await new RegionStore(_s).DeregisterAsync(A, Ct);
        await new RegionStore(_s).EraseAsync(A, Ct);
        Assert.Equal(0, (await new RegionStore(_s).SpaceAsync([A], Ct))[A]);
    }
}
