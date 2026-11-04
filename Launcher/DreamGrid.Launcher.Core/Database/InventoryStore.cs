using DreamGrid.Launcher.Core.Settings;
using MySqlConnector;

namespace DreamGrid.Launcher.Core.Database;

/// <summary>One of a person's inventory folders (OpenSim folder types: 8 My Inventory, 14 Trash, 16 Lost And Found).</summary>
public sealed record InventoryFolder(Guid Id, Guid? Parent, string Name, int Type);

/// <summary>
/// One <c>robust.inventoryitems</c> row, every column as OpenSim stores it, so a put-back writes back exactly what was there.
/// </summary>
public sealed record InventoryItemRow(string? AssetId, int? AssetType, string? Name, string? Description, uint? NextPermissions,
    uint? CurrentPermissions, int? InvType, string CreatorId, uint BasePermissions, uint EveryOnePermissions, int SalePrice, sbyte SaleType,
    int CreationDate, string GroupId, sbyte GroupOwned, uint Flags, Guid ItemId, Guid Owner, Guid Folder, uint GroupPermissions);

/// <summary>An item with its creator's name and the folder path it is in ("My Inventory / Clothes / Hats").</summary>
public sealed record InventoryItemView(InventoryItemRow Row, string CreatorName, string FolderPath);

/// <summary>An item someone holds, with their name.</summary>
public sealed record InventoryHolding(Guid Owner, string OwnerName, InventoryItemView Item);

/// <summary>
/// People's inventories in Robust's database, for takedowns (spec 2026-11-04-inventory-takedown-design). Every method throws
/// <see cref="StaffDirectoryUnavailableException"/> when the database can't be asked.
/// </summary>
public interface IInventoryStore
{
    Task<IReadOnlyList<InventoryFolder>> FoldersAsync(Guid owner, CancellationToken ct);

    /// <summary>The items in one folder, by name.</summary>
    Task<IReadOnlyList<InventoryItemView>> ItemsInAsync(Guid owner, Guid folder, int skip, int take, CancellationToken ct);

    /// <summary>The items anywhere in the inventory (Trash too) whose name contains <paramref name="text"/>, by name.</summary>
    Task<IReadOnlyList<InventoryItemView>> SearchAsync(Guid owner, string text, int skip, int take, CancellationToken ct);

    /// <summary>Those of <paramref name="items"/> that <paramref name="owner"/> holds.</summary>
    Task<IReadOnlyList<InventoryItemView>> ItemsAsync(Guid owner, IReadOnlyCollection<Guid> items, CancellationToken ct);

    /// <summary>Every item that isn't a link and has <paramref name="assetId"/>, in anyone's inventory, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<InventoryHolding>> CopiesAsync(string assetId, int max, CancellationToken ct);

    /// <summary>Deletes the items' rows and raises the version of each folder they were in, in one transaction.</summary>
    Task RemoveAsync(Guid owner, IReadOnlyCollection<Guid> items, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="row"/> back into its old folder if the owner still has it, else into their Lost And Found, else My
    /// Inventory, and raises that folder's version. Returns the folder used. Throws <see cref="NoInventoryException"/> when the
    /// owner has no folders at all (they were deleted).
    /// </summary>
    Task<Guid> PutBackAsync(InventoryItemRow row, CancellationToken ct);

    /// <summary>The asset's FSAssets hash, or null when it has no row (or the grid keeps no <c>fsassets</c> table).</summary>
    Task<string?> AssetHashAsync(string assetId, CancellationToken ct);
}

/// <summary>A put-back found no folders at all for the owner: they were deleted.</summary>
public sealed class NoInventoryException() : InvalidOperationException("They have no inventory folders.");

/// <summary><see cref="IInventoryStore"/> as robustuser, on OpenSim's own columns (XInventory's MySQL migrations).</summary>
public sealed class InventoryStore(Func<LauncherSettings> settings) : IInventoryStore
{
    private const uint TimeoutSeconds = 10;
    private const int RootType = 8;
    private const int LostAndFoundType = 16;

    private const string Columns =
        "i.assetID, i.assetType, i.inventoryName, i.inventoryDescription, i.inventoryNextPermissions, i.inventoryCurrentPermissions, " +
        "i.invType, i.creatorID, i.inventoryBasePermissions, i.inventoryEveryOnePermissions, i.salePrice, i.saleType, i.creationDate, " +
        "i.groupID, i.groupOwned, i.flags, i.inventoryID, i.avatarID, i.parentFolderID, i.inventoryGroupPermissions, " +
        "CONCAT(c.FirstName, ' ', c.LastName), CONCAT(o.FirstName, ' ', o.LastName)";

    private const string From =
        " FROM robust.inventoryitems i LEFT JOIN robust.UserAccounts c ON c.PrincipalID = LEFT(i.creatorID, 36) " +
        "LEFT JOIN robust.UserAccounts o ON o.PrincipalID = i.avatarID";

    public Task<IReadOnlyList<InventoryFolder>> FoldersAsync(Guid owner, CancellationToken ct) =>
        Run(async conn => (IReadOnlyList<InventoryFolder>)await FoldersAsync(conn, null, owner, ct), ct);

    public Task<IReadOnlyList<InventoryItemView>> ItemsInAsync(Guid owner, Guid folder, int skip, int take, CancellationToken ct) =>
        Run(conn => ViewsAsync(conn, owner, "i.avatarID = @o AND i.parentFolderID = @f ORDER BY i.inventoryName LIMIT @take OFFSET @skip", ct,
            ("@o", Id(owner)), ("@f", Id(folder)), ("@take", take), ("@skip", skip)), ct);

    public Task<IReadOnlyList<InventoryItemView>> SearchAsync(Guid owner, string text, int skip, int take, CancellationToken ct) =>
        Run(conn => ViewsAsync(conn, owner, "i.avatarID = @o AND i.inventoryName LIKE @q ESCAPE '\\\\' ORDER BY i.inventoryName LIMIT @take OFFSET @skip", ct,
            ("@o", Id(owner)), ("@q", PeopleDirectory.LikePattern(text)), ("@take", take), ("@skip", skip)), ct);

    public Task<IReadOnlyList<InventoryItemView>> ItemsAsync(Guid owner, IReadOnlyCollection<Guid> items, CancellationToken ct)
    {
        if (items.Count == 0) return Task.FromResult<IReadOnlyList<InventoryItemView>>([]);
        var names = items.Select((_, n) => "@i" + n).ToList();
        var args = new List<(string, object)> { ("@o", Id(owner)) };
        args.AddRange(items.Select((id, n) => ("@i" + n, (object)Id(id))));
        return Run(conn => ViewsAsync(conn, owner, $"i.avatarID = @o AND i.inventoryID IN ({string.Join(", ", names)}) ORDER BY i.inventoryName", ct,
            [.. args]), ct);
    }

    public Task<IReadOnlyList<InventoryHolding>> CopiesAsync(string assetId, int max, CancellationToken ct) =>
        Run(async conn =>
        {
            var rows = await RowsAsync(conn, null, "i.assetID = @a AND i.assetType NOT IN (24, 25) ORDER BY o.LastName, o.FirstName, i.inventoryName LIMIT @max",
                ct, ("@a", assetId), ("@max", max));
            var paths = new Dictionary<Guid, Dictionary<Guid, InventoryFolder>>();
            var list = new List<InventoryHolding>();
            foreach (var (row, creator, ownerName) in rows)
            {
                if (!paths.TryGetValue(row.Owner, out var folders))
                    paths[row.Owner] = folders = (await FoldersAsync(conn, null, row.Owner, ct)).ToDictionary(f => f.Id);
                list.Add(new InventoryHolding(row.Owner, ownerName ?? "unknown", new InventoryItemView(row, CreatorName(row.CreatorId, creator), PathOf(folders, row.Folder))));
            }
            return (IReadOnlyList<InventoryHolding>)list;
        }, ct);

    public Task RemoveAsync(Guid owner, IReadOnlyCollection<Guid> items, CancellationToken ct) =>
        Run(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var folders = new HashSet<string>();
            foreach (var item in items)
            {
                await using (var find = new MySqlCommand("SELECT parentFolderID FROM robust.inventoryitems WHERE inventoryID = @i AND avatarID = @o", conn, tx))
                {
                    find.Parameters.AddWithValue("@i", Id(item));
                    find.Parameters.AddWithValue("@o", Id(owner));
                    if (await find.ExecuteScalarAsync(ct) is string folder) folders.Add(folder);
                }
                await Exec(conn, tx, "DELETE FROM robust.inventoryitems WHERE inventoryID = @i AND avatarID = @o", ct, ("@i", Id(item)), ("@o", Id(owner)));
            }
            foreach (var folder in folders)
                await Exec(conn, tx, "UPDATE robust.inventoryfolders SET version = version + 1 WHERE folderID = @f", ct, ("@f", folder));
            await tx.CommitAsync(ct);
            return 0;
        }, ct);

    public Task<Guid> PutBackAsync(InventoryItemRow row, CancellationToken ct) =>
        Run(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var folders = await FoldersAsync(conn, tx, row.Owner, ct);
            var into = folders.FirstOrDefault(f => f.Id == row.Folder)
                       ?? folders.FirstOrDefault(f => f.Type == LostAndFoundType)
                       ?? folders.FirstOrDefault(f => f.Type == RootType)
                       ?? throw new NoInventoryException();
            await Exec(conn, tx,
                "INSERT INTO robust.inventoryitems (assetID, assetType, inventoryName, inventoryDescription, inventoryNextPermissions, " +
                "inventoryCurrentPermissions, invType, creatorID, inventoryBasePermissions, inventoryEveryOnePermissions, salePrice, saleType, " +
                "creationDate, groupID, groupOwned, flags, inventoryID, avatarID, parentFolderID, inventoryGroupPermissions) VALUES " +
                "(@as, @at, @n, @d, @np, @cp, @it, @c, @bp, @ep, @sp, @st, @cd, @g, @go, @fl, @i, @o, @f, @gp)", ct,
                ("@as", row.AssetId), ("@at", row.AssetType), ("@n", row.Name), ("@d", row.Description), ("@np", row.NextPermissions),
                ("@cp", row.CurrentPermissions), ("@it", row.InvType), ("@c", row.CreatorId), ("@bp", row.BasePermissions),
                ("@ep", row.EveryOnePermissions), ("@sp", row.SalePrice), ("@st", row.SaleType), ("@cd", row.CreationDate), ("@g", row.GroupId),
                ("@go", row.GroupOwned), ("@fl", row.Flags), ("@i", Id(row.ItemId)), ("@o", Id(row.Owner)), ("@f", Id(into.Id)),
                ("@gp", row.GroupPermissions));
            await Exec(conn, tx, "UPDATE robust.inventoryfolders SET version = version + 1 WHERE folderID = @f", ct, ("@f", Id(into.Id)));
            await tx.CommitAsync(ct);
            return into.Id;
        }, ct);

    public Task<string?> AssetHashAsync(string assetId, CancellationToken ct) =>
        Run(async conn =>
        {
            await using (var exists = new MySqlCommand(
                             "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'robust' AND TABLE_NAME = 'fsassets'", conn))
                if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0) return null;
            await using var cmd = new MySqlCommand("SELECT hash FROM robust.fsassets WHERE id = @a", conn);
            cmd.Parameters.AddWithValue("@a", assetId);
            return await cmd.ExecuteScalarAsync(ct) as string;
        }, ct);

    /// <summary>
    /// A creator as people read it: a local account's "First Last"; for a hypergrid creator (<c>uuid;http://grid/;First Last</c>)
    /// the name and the grid's host; "unknown" when neither is known.
    /// </summary>
    public static string CreatorName(string creatorId, string? localName)
    {
        if (!string.IsNullOrWhiteSpace(localName) && !creatorId.Contains(';')) return localName;
        var parts = creatorId.Split(';');
        if (parts.Length >= 3 && parts[2].Trim().Length > 0)
        {
            var host = Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) ? uri.Host : parts[1];
            return $"{parts[2].Trim()} ({host})";
        }
        return string.IsNullOrWhiteSpace(localName) ? "unknown" : localName;
    }

    /// <summary>"My Inventory / Clothes / Hats" for <paramref name="folder"/>; the root's own name is shown as My Inventory.</summary>
    public static string PathOf(IReadOnlyDictionary<Guid, InventoryFolder> folders, Guid folder)
    {
        var names = new List<string>();
        var seen = new HashSet<Guid>();
        for (Guid? at = folder; at is { } id && folders.TryGetValue(id, out var f) && seen.Add(id); at = f.Parent)
            names.Add(f.Type == RootType ? "My Inventory" : f.Name);
        if (names.Count == 0) return "(a folder that is gone)";
        names.Reverse();
        return string.Join(" / ", names);
    }

    private async Task<IReadOnlyList<InventoryItemView>> ViewsAsync(MySqlConnection conn, Guid owner, string where, CancellationToken ct,
        params (string Name, object? Value)[] args)
    {
        var rows = await RowsAsync(conn, null, where, ct, args);
        var folders = (await FoldersAsync(conn, null, owner, ct)).ToDictionary(f => f.Id);
        return [.. rows.Select(r => new InventoryItemView(r.Row, CreatorName(r.Row.CreatorId, r.Creator), PathOf(folders, r.Row.Folder)))];
    }

    private static async Task<List<(InventoryItemRow Row, string? Creator, string? OwnerName)>> RowsAsync(MySqlConnection conn, MySqlTransaction? tx,
        string where, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = new MySqlCommand($"SELECT {Columns}{From} WHERE {where}", conn, tx);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(InventoryItemRow, string?, string?)>();
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            uint? U(int i) => r.IsDBNull(i) ? null : r.GetUInt32(i);
            var row = new InventoryItemRow(S(0), I(1), S(2), S(3), U(4), U(5), I(6), r.GetString(7), r.GetUInt32(8), r.GetUInt32(9), r.GetInt32(10),
                r.GetSByte(11), r.GetInt32(12), r.GetString(13), r.GetSByte(14), r.GetUInt32(15), Guid.Parse(r.GetString(16)),
                Guid.TryParse(S(17), out var o) ? o : Guid.Empty, Guid.TryParse(S(18), out var f) ? f : Guid.Empty, r.GetUInt32(19));
            list.Add((row, S(20), S(21)));
        }
        return list;
    }

    private static async Task<List<InventoryFolder>> FoldersAsync(MySqlConnection conn, MySqlTransaction? tx, Guid owner, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT folderID, parentFolderID, folderName, type FROM robust.inventoryfolders WHERE agentID = @o", conn, tx);
        cmd.Parameters.AddWithValue("@o", Id(owner));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<InventoryFolder>();
        while (await r.ReadAsync(ct))
        {
            Guid? parent = !r.IsDBNull(1) && Guid.TryParse(r.GetString(1), out var p) && p != Guid.Empty ? p : null;
            list.Add(new InventoryFolder(Guid.Parse(r.GetString(0)), parent, r.IsDBNull(2) ? "" : r.GetString(2), r.GetInt16(3)));
        }
        return list;
    }

    private static async Task Exec(MySqlConnection conn, MySqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Id(Guid id) => id.ToString("D");

    private async Task<T> Run<T>(Func<MySqlConnection, Task<T>> work, CancellationToken ct)
    {
        try
        {
            var s = settings();
            await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds));
            await conn.OpenAsync(ct);
            return await work(conn);
        }
        catch (Exception e) when (e is MySqlException or InvalidCastException or FormatException or TimeoutException
                                      or System.Net.Sockets.SocketException || e is InvalidOperationException and not NoInventoryException)
        {
            throw new StaffDirectoryUnavailableException("The inventory could not be read or changed.", e);
        }
    }
}
