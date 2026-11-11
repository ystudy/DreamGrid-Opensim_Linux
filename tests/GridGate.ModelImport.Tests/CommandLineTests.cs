using System.Text.Json;
using Xunit;

namespace GridGate.ModelImport.Tests;

public sealed class CommandLineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-model-import-" + Guid.NewGuid().ToString("N"));
    private readonly string _out;

    public CommandLineTests()
    {
        Directory.CreateDirectory(_dir);
        _out = Path.Combine(_dir, "job");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "model-import", name);

    private int Check(string input, string? limits = null, string? options = null) =>
        Program.Main(new[] { "check", "--input", input, "--limits", limits ?? Fixture("limits.json"),
            "--options", options ?? Fixture("options.json"), "--out", _out });

    private T ReadJson<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(_out, name)), ContractJson.Options)!;

    [Fact]
    public void Check_writes_report_and_assets()
    {
        Assert.Equal(0, Check(GltfReaderTests.ModelPath("Box.glb")));

        var report = ReadJson<ImportReport>("report.json");
        Assert.False(report.Blocked);
        Assert.Equal(1, report.Version);

        var manifest = ReadJson<ImportManifest>("manifest.json");
        Assert.Single(manifest.Assets, a => a.Type == 49);
        var obj = Assert.Single(manifest.Assets, a => a.Type == 6);
        Assert.Equal(obj.Id, manifest.Item.AssetId);
        Assert.Equal("Castle", manifest.Item.Name);
        Assert.Equal($"Uploaded from castle.glb on {DateTime.UtcNow:yyyy-MM-dd}", manifest.Item.Description);
        Assert.Equal(new Guid("6f1c2a7e-3b4d-4e8a-9c21-5d7f0a8b9e10"), manifest.Item.CreatorId);
        foreach (var a in manifest.Assets)
        {
            Assert.Equal($"assets/{a.Id}.bin", a.File);
            Assert.True(new FileInfo(Path.Combine(_out, a.File)).Length > 0, $"{a.File} is missing or empty");
        }
        // Nothing but the report, the manifest and the listed assets; no temporary files left behind.
        Assert.Equal(new[] { "manifest.json", "report.json" }, Directory.GetFiles(_out).Select(Path.GetFileName).Order().ToArray());
        Assert.Equal(manifest.Assets.Count, Directory.GetFiles(Path.Combine(_out, "assets")).Length);
    }

    [Fact]
    public void Textured_model_lists_its_texture()
    {
        Assert.Equal(0, Check(GltfReaderTests.ModelPath("BoxTextured.glb")));
        var manifest = ReadJson<ImportManifest>("manifest.json");
        var texture = Assert.Single(manifest.Assets, a => a.Type == 0);
        Assert.Equal("Castle texture 1", texture.Name);
        Assert.Equal(1, ReadJson<ImportReport>("report.json").Summary.Textures);
    }

    [Fact]
    public void Blocked_writes_report_only()
    {
        // One triangle scaled to 300 m, above the 256 m largest part.
        var input = Path.Combine(_dir, "huge.gltf");
        File.WriteAllBytes(input, GltfReaderTests.GltfJson("[{\"mesh\":0,\"scale\":[300,300,300]}]", "[0]"));

        Assert.Equal(0, Check(input));
        var report = ReadJson<ImportReport>("report.json");
        Assert.True(report.Blocked);
        Assert.Contains(report.Findings, f => f.Code == "too-big");
        Assert.False(Directory.Exists(Path.Combine(_out, "assets")));
        Assert.False(File.Exists(Path.Combine(_out, "manifest.json")));
    }

    [Fact]
    public void Assets_over_the_cap_block_and_write_nothing_else()
    {
        var limits = Path.Combine(_dir, "limits.json");
        var json = JsonSerializer.Deserialize<ImportLimits>(File.ReadAllText(Fixture("limits.json")), ContractJson.Options)! with { MaxAssetBytes = 100 };
        File.WriteAllText(limits, JsonSerializer.Serialize(json, ContractJson.Options));

        Assert.Equal(0, Check(GltfReaderTests.ModelPath("BoxTextured.glb"), limits: limits));
        var report = ReadJson<ImportReport>("report.json");
        Assert.True(report.Blocked);
        var f = Assert.Single(report.Findings, f => f.Kind == FindingKind.Blocking);
        Assert.Equal("too-large-file", f.Code);
        Assert.Equal("assets", f.Values["what"]);
        Assert.Equal("100", f.Values["limit"]);
        Assert.False(Directory.Exists(Path.Combine(_out, "assets")));
        Assert.Equal(new[] { "report.json" }, Directory.GetFiles(_out).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Unreadable_model_writes_a_blocked_report()
    {
        var input = Path.Combine(_dir, "junk.glb");
        File.WriteAllText(input, "this is not a model");
        Assert.Equal(0, Check(input));
        var report = ReadJson<ImportReport>("report.json");
        Assert.True(report.Blocked);
        Assert.Contains(report.Findings, f => f.Code == "bad-file");
    }

    // The arguments as one line, split on spaces.
    [Theory]
    [InlineData("")]
    [InlineData("check")]
    [InlineData("convert --input a --limits b --options c --out d")]
    [InlineData("check --input a --limits b --options c")]
    [InlineData("check --input a --input a --limits b --options c --out d")]
    [InlineData("check --input a --limits b --options c --out d --colour red")]
    [InlineData("check --input --limits b --options c --out d")]
    public void Bad_arguments_exit_2(string line) =>
        Assert.Equal(2, Program.Main(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Unreadable_limits_exit_2()
    {
        var limits = Path.Combine(_dir, "limits.json");
        File.WriteAllText(limits, "{ not json");
        Assert.Equal(2, Check(GltfReaderTests.ModelPath("Box.glb"), limits: limits));
        Assert.False(File.Exists(Path.Combine(_out, "report.json")));
    }

    [Fact]
    public void Missing_options_file_exits_2()
    {
        Assert.Equal(2, Check(GltfReaderTests.ModelPath("Box.glb"), options: Path.Combine(_dir, "nowhere.json")));
        Assert.False(File.Exists(Path.Combine(_out, "report.json")));
    }

    [Fact]
    public void Options_without_an_item_name_exit_2()
    {
        var options = Path.Combine(_dir, "options.json");
        File.WriteAllText(options, "{\"fileName\":\"a.glb\",\"scalePercent\":100}");
        Assert.Equal(2, Check(GltfReaderTests.ModelPath("Box.glb"), options: options));
    }
}
