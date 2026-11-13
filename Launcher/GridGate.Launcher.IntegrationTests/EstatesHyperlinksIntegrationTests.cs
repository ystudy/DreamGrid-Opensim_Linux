using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Estates;
using GridGate.Launcher.Core.Hypergrid;
using GridGate.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// <see cref="GridEstates"/> and <see cref="GridHyperlinks"/> on a real MariaDB, as the users the launcher connects as, with
/// OpenSim's own table and column names (EstateStore.migrations, GridStore.migrations); columns nothing reads are left out.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class EstatesHyperlinksIntegrationTests : IAsyncDisposable
{
    private static readonly Guid Owner = Guid.Parse("0e000000-0000-0000-0000-00000000000e");
    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid RegionA = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Link = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-estates-" + Guid.NewGuid().ToString("N"));
    private Process? _server;
    private MariaDbServer? _maria;
    private LauncherSettings _s = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        await new DatabaseProvisioner(_s).EnsureAsync(Ct);
        await Sql(Tables);
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

    private const string Tables = """
        CREATE TABLE `opensim`.`estate_managers` (`EstateID` int(10) unsigned NOT NULL, `uuid` char(36) NOT NULL, KEY `EstateID` (`EstateID`)) ENGINE=InnoDB DEFAULT CHARSET=utf8;
        CREATE TABLE `opensim`.`estate_map` (`RegionID` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000', `EstateID` int(11) NOT NULL, PRIMARY KEY (`RegionID`), KEY `EstateID` (`EstateID`)) ENGINE=InnoDB DEFAULT CHARSET=utf8;
        CREATE TABLE `opensim`.`estate_settings` (`EstateID` int(10) unsigned NOT NULL AUTO_INCREMENT, `EstateName` varchar(64) DEFAULT NULL, `EstateOwner` varchar(36) NOT NULL, PRIMARY KEY (`EstateID`)) ENGINE=InnoDB AUTO_INCREMENT=101 DEFAULT CHARSET=utf8;
        CREATE TABLE `robust`.`UserAccounts` (`PrincipalID` char(36) NOT NULL, `FirstName` varchar(64) NOT NULL, `LastName` varchar(64) NOT NULL, PRIMARY KEY (`PrincipalID`)) ENGINE=InnoDB DEFAULT CHARSET=utf8;
        CREATE TABLE `robust`.`regions` (`uuid` varchar(36) NOT NULL, `regionName` varchar(128) DEFAULT NULL, `serverURI` varchar(255) DEFAULT NULL, `locX` int(10) unsigned DEFAULT NULL, `locY` int(10) unsigned DEFAULT NULL, `sizeX` int(11) NOT NULL DEFAULT '0', `sizeY` int(11) NOT NULL DEFAULT '0', `flags` int(11) NOT NULL DEFAULT '0', PRIMARY KEY (`uuid`)) ENGINE=InnoDB DEFAULT CHARSET=utf8;
        """;

    [Fact]
    public async Task Estates_are_read_and_changed_the_way_OpenSim_writes_them()
    {
        await StartAsync();
        await Sql($"""
            INSERT INTO `robust`.`UserAccounts` VALUES ('{Owner}', 'Test', 'Owner'), ('{Ann}', 'Ann', 'Smith'), ('{Bob}', 'Bob', 'Jones');
            INSERT INTO `opensim`.`estate_settings` (`EstateName`, `EstateOwner`) VALUES ('Avalon Estate', '{Owner}'), ('Spare', '{Owner}');
            INSERT INTO `opensim`.`estate_map` VALUES ('{RegionA}', 101);
            """);
        var estates = new GridEstates(() => _s);

        var list = await estates.ListAsync(Ct);
        Assert.Equal(["Avalon Estate", "Spare"], list.Select(e => e.Name));
        Assert.Equal("Test Owner", list[0].OwnerName);
        Assert.Equal(1, list[0].Regions);
        Assert.Equal(new Dictionary<Guid, int> { [RegionA] = 101 }, await estates.RegionEstatesAsync(Ct));

        Assert.True(await estates.NameTakenAsync("spare", null, Ct));
        Assert.False(await estates.NameTakenAsync("spare", 102, Ct));
        Assert.Equal(EstateChange.NameTaken, await estates.RenameAsync(102, "AVALON ESTATE", Ct));
        Assert.Equal(EstateChange.Done, await estates.RenameAsync(102, "Sandbox", Ct));
        Assert.Equal(EstateChange.NoEstate, await estates.RenameAsync(999, "Nope", Ct));

        Assert.Equal(EstateChange.Done, await estates.AddManagerAsync(102, Ann, Ct));
        Assert.Equal(EstateChange.AlreadyManager, await estates.AddManagerAsync(102, Ann, Ct));
        Assert.Equal(EstateChange.Done, await estates.AddManagerAsync(102, Bob, Ct));
        // Making a manager the owner takes them off the managers.
        Assert.Equal(EstateChange.Done, await estates.SetOwnerAsync(102, Bob, Ct));
        var spare = await estates.GetAsync(102, Ct);
        Assert.NotNull(spare);
        Assert.Equal("Sandbox", spare.Name);
        Assert.Equal(Bob, spare.Owner);
        Assert.Equal("Bob Jones", spare.OwnerName);
        Assert.Equal("Ann Smith", Assert.Single(spare.Managers).Name);
        Assert.Empty(spare.Regions);

        Assert.Equal(EstateChange.Done, await estates.RemoveManagerAsync(102, Ann, Ct));
        Assert.Equal(EstateChange.NotAManager, await estates.RemoveManagerAsync(102, Ann, Ct));
        for (var i = 0; i < GridEstates.MaxManagers; i++) Assert.Equal(EstateChange.Done, await estates.AddManagerAsync(102, Guid.NewGuid(), Ct));
        Assert.Equal(EstateChange.TooManyManagers, await estates.AddManagerAsync(102, Ann, Ct));
        Assert.Null(await estates.GetAsync(999, Ct));
    }

    [Fact]
    public async Task Hyperlinks_are_listed_and_only_a_hyperlink_row_is_removed()
    {
        await StartAsync();
        var local = Guid.NewGuid();
        await Sql($"""
            INSERT INTO `robust`.`regions` (`uuid`, `regionName`, `serverURI`, `locX`, `locY`, `sizeX`, `sizeY`, `flags`) VALUES
              ('{local}', 'Home', 'http://127.0.0.1:9000/', {1000 * 256}, {1000 * 256}, 512, 512, 4),
              ('{Link}', 'Far Place', 'http://far.example.net:8002/', {1000 * 256}, {1003 * 256}, 256, 256, {512 + 8 + 4});
            """);
        var links = new GridHyperlinks(() => _s);

        var link = Assert.Single(await links.ListAsync(Ct));
        Assert.Equal(new Hyperlink(Link, "Far Place", "http://far.example.net:8002/", 1000, 1003), link);
        var map = await links.MapAsync(Ct);
        Assert.Contains(new MapSpace(1000, 1000, 2, 2, false), map);
        Assert.Contains(new MapSpace(1000, 1003, 1, 1, true), map);
        Assert.Equal((1001, 1003), HyperlinkPlace.Free(map));

        Assert.False(await links.RemoveAsync(local, Ct));
        Assert.True(await links.RemoveAsync(Link, Ct));
        Assert.Empty(await links.ListAsync(Ct));
        Assert.Single(await links.MapAsync(Ct));
    }

    [Fact]
    public async Task A_database_that_is_down_is_its_own_exception()
    {
        _s = new LauncherSettings
        {
            Db = new DatabaseSecrets(Password(), Password(), Password()),
            Console = new ConsoleSecrets("u", Password()),
            MachineId = "test",
            MariaDbPort = FreePort(),
        };
        await Assert.ThrowsAsync<EstatesUnavailableException>(() => new GridEstates(() => _s).ListAsync(Ct));
        await Assert.ThrowsAsync<HyperlinksUnavailableException>(() => new GridHyperlinks(() => _s).ListAsync(Ct));
    }

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

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Password() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
}
