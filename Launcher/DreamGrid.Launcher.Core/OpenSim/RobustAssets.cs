using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.OpenSim;

/// <summary>Robust can't store an asset or an inventory entry (it isn't running, it refused, or its reply made no sense).</summary>
public sealed class RobustAssetsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Stores assets and inventory items in Robust over its private port, with no region involved.</summary>
public interface IRobustAssets
{
    /// <summary>Stores an asset under <paramref name="id"/>. Throws <see cref="RobustAssetsUnavailableException"/> when it was not stored.</summary>
    Task StoreAsync(Guid id, int type, string name, byte[] data, Guid creator, CancellationToken ct);

    /// <summary>The id of the owner's "Uploads" folder under the inventory root, made when it isn't there.</summary>
    Task<Guid> UploadsFolderAsync(Guid owner, CancellationToken ct);

    /// <summary>Puts an object item that points at an already stored asset into <paramref name="folder"/>.</summary>
    Task AddItemAsync(Guid owner, Guid folder, ManifestItem item, CancellationToken ct);
}

/// <summary>
/// <see cref="IRobustAssets"/> on Robust's private services: <c>POST /assets</c> (<c>AssetServerPostHandler</c>, an
/// <c>AssetBase</c> in XML) and <c>POST /xinventory</c> (<c>XInventoryInConnector</c>, form fields). The settings are read per call.
/// </summary>
public sealed class RobustAssets(HttpClient http, Func<LauncherSettings> settings) : IRobustAssets
{
    // OpenSim's PermissionMask (OpenSim/Framework/Util.cs): All = Transfer | Modify | Copy | Move, and Export is
    // given separately. The launcher can't reference OpenSim, so the numbers are written out.
    private const uint AllAndExport = 0x9e000; // All 0x8e000 | Export 0x10000

    // AssetType.Object and InventoryType.Object (both 6 in OpenMetaverse).
    private const int ObjectAssetType = 6;
    private const int ObjectInventoryType = 6;

    private const string UploadsName = "Uploads";

    // A user-made folder: InventoryFolderBase.Type -1 ("no preferred type").
    private const int PlainFolderType = -1;

    private Uri PrivateBase => GridEndpoints.PrivateBase(settings());

    public async Task StoreAsync(Guid id, int type, string name, byte[] data, Guid creator, CancellationToken ct)
    {
        var asset = new XElement("AssetBase",
            new XElement("Data", Convert.ToBase64String(data)),
            new XElement("FullID", Id(id)),
            new XElement("ID", Id(id)),
            new XElement("Name", name),
            new XElement("Description", ""),
            new XElement("Type", type.ToString(CultureInfo.InvariantCulture)),
            new XElement("Local", "false"),
            new XElement("Temporary", "false"),
            new XElement("CreatorID", Id(creator)),
            new XElement("Flags", "Normal"));

        var reply = await Send("/assets", new StringContent(asset.ToString(SaveOptions.DisableFormatting), System.Text.Encoding.UTF8, "text/xml"), ct);
        // The handler answers <string>id</string>; the nil id means the database refused the asset.
        if (!Guid.TryParse(reply.Value.Trim(), out var stored) || stored != id)
            throw new RobustAssetsUnavailableException("The asset service did not store the asset.");
    }

    public async Task<Guid> UploadsFolderAsync(Guid owner, CancellationToken ct)
    {
        var rootReply = await Inventory(ct, ("METHOD", "GETROOTFOLDER"), ("PRINCIPAL", Id(owner)));
        var root = Guid.TryParse(rootReply.Element("folder")?.Element("ID")?.Value.Trim(), out var r) ? r
            : throw new RobustAssetsUnavailableException("The inventory service has no root folder for this account.");

        var content = await Inventory(ct, ("METHOD", "GETFOLDERCONTENT"), ("PRINCIPAL", Id(owner)), ("FOLDER", Id(root)));
        foreach (var folder in content.Element("FOLDERS")?.Elements() ?? [])
            if (folder.Element("Name")?.Value == UploadsName && Guid.TryParse(folder.Element("ID")?.Value.Trim(), out var found))
                return found;

        var id = Guid.NewGuid();
        await InventoryDone(ct,
            ("METHOD", "ADDFOLDER"), ("ID", Id(id)), ("ParentID", Id(root)), ("Name", UploadsName),
            ("Type", PlainFolderType.ToString(CultureInfo.InvariantCulture)), ("Version", "1"), ("Owner", Id(owner)));
        return id;
    }

    public Task AddItemAsync(Guid owner, Guid folder, ManifestItem item, CancellationToken ct) =>
        InventoryDone(ct,
            ("METHOD", "ADDITEM"),
            ("ID", Id(Guid.NewGuid())),
            ("AssetID", Id(item.AssetId)),
            ("AssetType", ObjectAssetType.ToString(CultureInfo.InvariantCulture)),
            ("InvType", ObjectInventoryType.ToString(CultureInfo.InvariantCulture)),
            ("Name", item.Name),
            ("Description", item.Description),
            ("CreatorId", Id(item.CreatorId)),
            ("CreatorData", ""),
            ("Owner", Id(owner)),
            ("Folder", Id(folder)),
            ("BasePermissions", AllAndExport.ToString(CultureInfo.InvariantCulture)),
            ("CurrentPermissions", AllAndExport.ToString(CultureInfo.InvariantCulture)),
            ("NextPermissions", item.NextOwnerMask.ToString(CultureInfo.InvariantCulture)),
            ("EveryOnePermissions", "0"),
            ("GroupPermissions", "0"),
            ("GroupID", Id(Guid.Empty)),
            ("GroupOwned", "False"),
            ("SalePrice", "0"),
            ("SaleType", "0"),
            ("Flags", "0"),
            ("CreationDate", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

    private static string Id(Guid id) => id.ToString("D");

    private async Task<XElement> Inventory(CancellationToken ct, params (string Key, string Value)[] fields)
    {
        using var form = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return await Send("/xinventory", form, ct);
    }

    /// <summary>An inventory call that answers <c>RESULT</c> True or False.</summary>
    private async Task InventoryDone(CancellationToken ct, params (string Key, string Value)[] fields)
    {
        var reply = await Inventory(ct, fields);
        if (!string.Equals(reply.Element("RESULT")?.Value.Trim(), "True", StringComparison.OrdinalIgnoreCase))
            throw new RobustAssetsUnavailableException("The inventory service refused the change.");
    }

    private async Task<XElement> Send(string path, HttpContent body, CancellationToken ct)
    {
        try
        {
            using var resp = await http.PostAsync(new Uri(PrivateBase, path), body, ct);
            resp.EnsureSuccessStatusCode();
            return XDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).Root
                ?? throw new RobustAssetsUnavailableException("Robust's reply is empty.");
        }
        catch (Exception e) when (e is HttpRequestException or XmlException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new RobustAssetsUnavailableException("Robust could not be reached or its reply was not understood.", e);
        }
    }
}
