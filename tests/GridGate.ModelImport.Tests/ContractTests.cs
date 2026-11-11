using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GridGate.ModelImport.Tests;

public class ContractTests
{
    // The fixtures are copied to fixtures/model-import under the test output folder (see the csproj).
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "model-import", name);

    [Fact]
    public void Fixtures_round_trip_unchanged()
    {
        AssertRoundTrip<ImportLimits>("limits.json");
        AssertRoundTrip<ImportOptions>("options.json");
        AssertRoundTrip<ImportReport>("report.json");
        // A null stays JSON null (never omitted): the launcher writes and reads the same shape.
        AssertRoundTrip<ImportReport>("report-fits.json");
        AssertRoundTrip<ImportManifest>("manifest.json");
    }

    [Fact]
    public void Fixture_manifest_mask_is_the_mask_of_the_fixture_options()
    {
        // The launcher's tests pin the same number for the same two files, so the two mask functions cannot drift apart.
        var options = JsonSerializer.Deserialize<ImportOptions>(File.ReadAllText(FixturePath("options.json")), ContractJson.Options)!;
        var manifest = JsonSerializer.Deserialize<ImportManifest>(File.ReadAllText(FixturePath("manifest.json")), ContractJson.Options)!;
        Assert.Equal(573440u, Building.ObjectAssetBuilder.NextOwnerMask(options.NextOwner)); // Move | Copy | Modify
        Assert.Equal(573440u, manifest.Item.NextOwnerMask);
    }

    private static void AssertRoundTrip<T>(string name)
    {
        var text = File.ReadAllText(FixturePath(name));
        var original = JsonNode.Parse(text);
        var model = JsonSerializer.Deserialize<T>(text, ContractJson.Options);
        Assert.NotNull(model);

        var again = JsonSerializer.SerializeToNode(model, ContractJson.Options);
        Assert.True(JsonNode.DeepEquals(original, again),
            $"{name} did not round-trip. Re-serialized as: {again?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
    }
}
