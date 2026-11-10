using System.Net;
using System.Xml.Linq;
using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class RobustAssetsTests
{
    private static readonly LauncherSettings Defaults =
        new SettingsStore(Path.Combine(Path.GetTempPath(), "dg-assets-" + Guid.NewGuid().ToString("N"), "settings.json")).LoadOrCreate();

    private static readonly Guid Owner = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Root = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Existing = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private const string Ok = "<?xml version=\"1.0\"?><ServerResponse><RESULT>True</RESULT></ServerResponse>";
    private const string Fail = "<?xml version=\"1.0\"?><ServerResponse><RESULT>False</RESULT></ServerResponse>";

    private static string RootReply(Guid root) =>
        $"<ServerResponse><folder type=\"List\"><ParentID>{Guid.Empty}</ParentID><Type>8</Type><Version>3</Version><Name>My Inventory</Name><Owner>{Owner}</Owner><ID>{root}</ID></folder></ServerResponse>";

    private static string ContentReply(params (string Name, Guid Id)[] folders) =>
        $"<ServerResponse><FID>{Root}</FID><VERSION>3</VERSION><FOLDERS type=\"List\">"
        + string.Concat(folders.Select((f, i) => $"<folder_{i} type=\"List\"><ParentID>{Root}</ParentID><Type>-1</Type><Version>1</Version><Name>{f.Name}</Name><Owner>{Owner}</Owner><ID>{f.Id}</ID></folder_{i}>"))
        + "</FOLDERS><ITEMS type=\"List\"></ITEMS></ServerResponse>";

    private static (RobustAssets Assets, StubHandler Handler) Make(Func<RecordedRequest, (HttpStatusCode, string)> reply)
    {
        var h = new StubHandler(reply);
        return (new RobustAssets(new HttpClient(h), () => Defaults), h);
    }

    private static (HttpStatusCode, string) Inventory(RecordedRequest r, string content) => r.Form("METHOD") switch
    {
        "GETROOTFOLDER" => (HttpStatusCode.OK, RootReply(Root)),
        "GETFOLDERCONTENT" => (HttpStatusCode.OK, content),
        _ => (HttpStatusCode.OK, Ok),
    };

    private static ManifestItem AnItem() => new(Guid.NewGuid(), "x", "", 0, Guid.NewGuid());

    [Fact]
    public async Task Store_posts_asset_xml()
    {
        var id = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var (assets, h) = Make(r => (HttpStatusCode.OK, $"<?xml version=\"1.0\"?><string>{id}</string>"));

        await assets.StoreAsync(id, 49, "Chair <1> & co", [1, 2, 3, 250], creator, CancellationToken.None);

        var req = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal($"http://127.0.0.1:{Defaults.RobustPrivatePort}/assets", req.Uri.ToString());
        var a = XDocument.Parse(req.Body).Root!;
        Assert.Equal("AssetBase", a.Name.LocalName);
        string V(string n) => a.Element(n)!.Value;
        Assert.Equal(id, Guid.Parse(V("FullID")));
        Assert.Equal(id.ToString("D"), V("ID"));
        Assert.Equal("Chair <1> & co", V("Name"));
        Assert.Equal("", V("Description"));
        Assert.Equal("49", V("Type"));
        Assert.Equal("false", V("Local"));
        Assert.Equal("false", V("Temporary"));
        Assert.Equal(creator.ToString("D"), V("CreatorID"));
        Assert.Equal("Normal", V("Flags"));
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 250]), V("Data"));
    }

    [Fact]
    public async Task Store_reply_with_another_id_throws_unavailable()
    {
        var (assets, _) = Make(r => (HttpStatusCode.OK, $"<string>{Guid.Empty}</string>"));
        await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() =>
            assets.StoreAsync(Guid.NewGuid(), 6, "x", [1], Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Uploads_folder_is_found_not_duplicated()
    {
        var (assets, h) = Make(r => Inventory(r, ContentReply(("Clothing", Guid.NewGuid()), ("Uploads", Existing))));

        var id = await assets.UploadsFolderAsync(Owner, CancellationToken.None);

        Assert.Equal(Existing, id);
        Assert.Equal(["GETROOTFOLDER", "GETFOLDERCONTENT"], h.Requests.Select(q => q.Form("METHOD")));
        Assert.Equal(Owner.ToString("D"), h.Requests[0].Form("PRINCIPAL"));
        Assert.Equal(Owner.ToString("D"), h.Requests[1].Form("PRINCIPAL"));
        Assert.Equal(Root.ToString("D"), h.Requests[1].Form("FOLDER"));
        Assert.All(h.Requests, q => Assert.EndsWith("/xinventory", q.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Uploads_folder_is_created_when_missing()
    {
        var (assets, h) = Make(r => Inventory(r, ContentReply(("Clothing", Guid.NewGuid()))));

        var id = await assets.UploadsFolderAsync(Owner, CancellationToken.None);

        var add = h.Requests[^1];
        Assert.Equal("ADDFOLDER", add.Form("METHOD"));
        Assert.Equal(id, Guid.Parse(add.Form("ID")));
        Assert.Equal(Root.ToString("D"), add.Form("ParentID"));
        Assert.Equal("Uploads", add.Form("Name"));
        Assert.Equal("-1", add.Form("Type"));
        Assert.Equal("1", add.Form("Version"));
        Assert.Equal(Owner.ToString("D"), add.Form("Owner"));
    }

    [Fact]
    public async Task Uploads_folder_with_no_folders_listed_is_created()
    {
        var (assets, h) = Make(r => Inventory(r, $"<ServerResponse><FID>{Root}</FID><VERSION>3</VERSION></ServerResponse>"));
        await assets.UploadsFolderAsync(Owner, CancellationToken.None);
        Assert.Equal("ADDFOLDER", h.Requests[^1].Form("METHOD"));
    }

    [Fact]
    public async Task Uploads_folder_add_refused_throws_unavailable()
    {
        var (assets, _) = Make(r => r.Form("METHOD") == "ADDFOLDER" ? (HttpStatusCode.OK, Fail) : Inventory(r, ContentReply()));
        await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() => assets.UploadsFolderAsync(Owner, CancellationToken.None));
    }

    [Fact]
    public async Task Uploads_folder_without_a_root_folder_throws_unavailable()
    {
        var (assets, _) = Make(r => (HttpStatusCode.OK, "<ServerResponse />"));
        await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() => assets.UploadsFolderAsync(Owner, CancellationToken.None));
    }

    [Fact]
    public async Task Add_item_sends_object_type_and_permissions()
    {
        var asset = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var folder = Guid.NewGuid();
        var item = new ManifestItem(asset, "Chair", "A chair", 0x82000, creator);
        var (assets, h) = Make(r => (HttpStatusCode.OK, Ok));

        await assets.AddItemAsync(Owner, folder, item, CancellationToken.None);

        var q = Assert.Single(h.Requests);
        Assert.EndsWith("/xinventory", q.Uri.AbsolutePath);
        Assert.Equal("ADDITEM", q.Form("METHOD"));
        Assert.Equal("6", q.Form("AssetType"));
        Assert.Equal("6", q.Form("InvType"));
        Assert.Equal(asset.ToString("D"), q.Form("AssetID"));
        Assert.NotEqual(Guid.Empty, Guid.Parse(q.Form("ID")));
        Assert.NotEqual(asset, Guid.Parse(q.Form("ID")));
        Assert.Equal("Chair", q.Form("Name"));
        Assert.Equal("A chair", q.Form("Description"));
        Assert.Equal(creator.ToString("D"), q.Form("CreatorId"));
        Assert.Equal("", q.Form("CreatorData"));
        Assert.Equal(Owner.ToString("D"), q.Form("Owner"));
        Assert.Equal(folder.ToString("D"), q.Form("Folder"));
        Assert.Equal("647168", q.Form("BasePermissions")); // 0x9e000: Transfer, Modify, Copy, Export, Move
        Assert.Equal("647168", q.Form("CurrentPermissions"));
        Assert.Equal(0x82000.ToString(), q.Form("NextPermissions"));
        Assert.Equal("0", q.Form("EveryOnePermissions"));
        Assert.Equal("0", q.Form("GroupPermissions"));
        Assert.Equal(Guid.Empty.ToString("D"), q.Form("GroupID"));
        Assert.Equal("False", q.Form("GroupOwned"));
        Assert.Equal("0", q.Form("SalePrice"));
        Assert.Equal("0", q.Form("SaleType"));
        Assert.Equal("0", q.Form("Flags"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(long.Parse(q.Form("CreationDate")), now - 60, now + 60);
    }

    [Fact]
    public async Task Add_item_refused_throws_unavailable()
    {
        var (assets, _) = Make(r => (HttpStatusCode.OK, Fail));
        await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() =>
            assets.AddItemAsync(Owner, Guid.NewGuid(), AnItem(), CancellationToken.None));
    }

    [Fact]
    public async Task Robust_down_throws_unavailable()
    {
        var assets = new RobustAssets(new HttpClient(new ThrowingHandler()), () => Defaults);
        var ex = await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() =>
            assets.StoreAsync(Guid.NewGuid(), 6, "x", [1], Guid.NewGuid(), CancellationToken.None));
        Assert.IsType<HttpRequestException>(ex.InnerException);
        await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() => assets.UploadsFolderAsync(Owner, CancellationToken.None));
        await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() =>
            assets.AddItemAsync(Owner, Guid.NewGuid(), AnItem(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "oops")]
    [InlineData(HttpStatusCode.BadRequest, "")]
    [InlineData(HttpStatusCode.OK, "not xml at all")]
    public async Task Bad_replies_throw_unavailable(HttpStatusCode status, string body)
    {
        var (assets, _) = Make(r => (status, body));
        await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() =>
            assets.StoreAsync(Guid.NewGuid(), 6, "x", [1], Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() => assets.UploadsFolderAsync(Owner, CancellationToken.None));
        await Assert.ThrowsAsync<RobustAssetsUnavailableException>(() =>
            assets.AddItemAsync(Owner, Guid.NewGuid(), AnItem(), CancellationToken.None));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
