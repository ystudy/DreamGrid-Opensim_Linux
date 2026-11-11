using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GridGate.ModelImport.Checking;
using GridGate.ModelImport.Model;
using GridGate.ModelImport.Reading;

namespace GridGate.ModelImport.Building;

/// <summary>
/// One conversion: reads the model, checks it, builds every asset and writes the job folder. report.json is always written;
/// assets/&lt;id&gt;.bin and manifest.json only when nothing blocks, the assets' total size included. Nothing is written outside the job folder, and the report and
/// manifest appear whole or not at all (written under a temporary name, then renamed); the manifest goes first, the report last.
/// </summary>
public static class JobWriter
{
    public const int ReportWritten = 0;
    public const int BadArguments = 2;
    public const int Failed = 3;

    private const string AssetFolder = "assets";

    public static int Run(string input, string limitsFile, string optionsFile, string outDir)
    {
        ImportLimits limits;
        ImportOptions options;
        try
        {
            limits = Load<ImportLimits>(limitsFile);
            options = Load<ImportOptions>(optionsFile);
            if (string.IsNullOrWhiteSpace(options.ItemName) || options.FileName == null || options.NextOwner == null)
                throw new InvalidDataException($"{optionsFile} needs itemName, fileName and nextOwner.");
            if (!File.Exists(input)) throw new FileNotFoundException($"{input} does not exist.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"GridGate.ModelImport: {e.Message}");
            return BadArguments;
        }

        try
        {
            Convert(input, limits, options, outDir);
            return ReportWritten;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"GridGate.ModelImport: the conversion failed: {e}");
            return Failed;
        }
    }

    private static T Load<T>(string file) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(file), ContractJson.Options) ?? throw new InvalidDataException($"{file} is empty.");

    private static void Convert(string input, ImportLimits limits, ImportOptions options, string outDir)
    {
        Directory.CreateDirectory(outDir);

        ReadResult read;
        using (var stream = File.OpenRead(input))
            read = GltfReader.Read(stream, options.FileName, limits);
        if (read.Model == null)
        {
            var nothing = new ReportSummary(0, 0, 0, 0, new double[3], null);
            WriteJson(outDir, "report.json", new ImportReport(1, true, nothing, read.Findings));
            return;
        }

        var check = ModelChecker.Check(read.Model, limits, options);
        var findings = read.Findings.Concat(check.Findings).ToList();
        if (check.Blocked)
        {
            WriteJson(outDir, "report.json", new ImportReport(1, true, check.Summary, findings));
            return;
        }

        var model = check.Fixed;
        var item = options.ItemName;
        var assets = new List<(ManifestAsset Asset, byte[] Bytes)>();

        // Textures: each image a kept material uses, once. An image that cannot be used leaves its faces untextured.
        var textureIdByImage = new Dictionary<int, Guid>();
        var images = model.Parts.SelectMany(p => p.Mesh.Faces).Select(f => f.Material).Distinct()
            .Where(m => m >= 0 && m < model.Materials.Count).Select(m => model.Materials[m].BaseColourImage).OfType<int>()
            .Where(i => i >= 0 && i < model.Images.Count).Distinct().OrderBy(i => i);
        foreach (var i in images)
        {
            var texture = TextureBuilder.Build(model.Images[i], limits.MaxTextureSide);
            if (texture.Finding != null) findings.Add(texture.Finding);
            if (texture.J2k.Length == 0) continue;
            var id = Guid.NewGuid();
            textureIdByImage[i] = id;
            assets.Add((Asset(id, 0, $"{item} texture {textureIdByImage.Count}"), texture.J2k));
        }

        // Meshes: one per part, fitted to the unit box the prim's scale stretches. No rigged items in this version.
        var meshIds = new List<Guid>();
        foreach (var part in model.Parts)
        {
            var id = Guid.NewGuid();
            meshIds.Add(id);
            var bytes = MeshAssetWriter.Write(UnitMesh.Fit(part, limits.MinPartSize), new MeshAssetOptions(WriteSkin: false, null));
            assets.Add((Asset(id, 49, $"{item} mesh {meshIds.Count}"), bytes));
        }

        var objectId = Guid.NewGuid();
        var xml = ObjectAssetBuilder.Build(model, meshIds, textureIdByImage, options, limits);
        assets.Add((Asset(objectId, 6, item), new UTF8Encoding(false).GetBytes(xml)));

        // The assets together, before anything is written: over the cap, the report says so and nothing else is written.
        var summary = check.Summary with { Textures = textureIdByImage.Count };
        if (assets.Sum(a => (long)a.Bytes.Length) > limits.MaxAssetBytes)
        {
            findings.Add(new Finding("too-large-file", FindingKind.Blocking, new Dictionary<string, string>
            {
                ["what"] = "assets",
                ["limit"] = limits.MaxAssetBytes.ToString(CultureInfo.InvariantCulture),
            }));
            WriteJson(outDir, "report.json", new ImportReport(1, true, summary, findings));
            return;
        }

        var assetDir = Path.Combine(outDir, AssetFolder);
        Directory.CreateDirectory(assetDir);
        foreach (var (asset, bytes) in assets)
            File.WriteAllBytes(Path.Combine(assetDir, FileName(asset.Id)), bytes);

        var description = $"Uploaded from {options.FileName} on {DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        var manifestItem = new ManifestItem(objectId, item, description, ObjectAssetBuilder.NextOwnerMask(options.NextOwner), options.UploaderId);
        WriteJson(outDir, "manifest.json", new ImportManifest(1, assets.Select(a => a.Asset).ToList(), manifestItem));

        WriteJson(outDir, "report.json", new ImportReport(1, false, summary, findings));
    }

    private static string FileName(Guid id) => id.ToString("D") + ".bin";

    private static ManifestAsset Asset(Guid id, int type, string name) => new(id, type, name, AssetFolder + "/" + FileName(id));

    // Written under a temporary name in the same folder, then renamed over the real name, so a reader never sees half a file.
    private static void WriteJson<T>(string outDir, string name, T value)
    {
        var final = Path.Combine(outDir, name);
        var temporary = Path.Combine(outDir, name + ".tmp");
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options));
        File.Move(temporary, final, overwrite: true);
    }
}
