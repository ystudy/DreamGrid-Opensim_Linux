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
        AssertRoundTrip<ImportManifest>("manifest.json");
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
