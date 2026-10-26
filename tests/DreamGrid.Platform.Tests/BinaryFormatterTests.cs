using OpenMetaverse;
using OpenSim.Framework;
using System.Runtime.Serialization.Formatters.Binary;
using Xunit;

namespace DreamGrid.Platform.Tests;

/// <summary>
/// .NET 9+ removed BinaryFormatter from the runtime: it still compiles, but throws PlatformNotSupportedException.
/// OpenSim uses it for the Flotsam asset disk cache, YEngine script state and keyframe motion, and swallows the
/// exception, so the failure is silent. These tests pin that the compatibility package makes it work again.
/// </summary>
public class BinaryFormatterTests
{
    [Fact]
    public void Util_SerializeToFile_then_DeserializeFromFile_round_trips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dg bf {Guid.NewGuid()}.bin");
        var original = new Dictionary<string, int> { ["alpha"] = 1, ["beta"] = 2 };
        try
        {
            Util.SerializeToFile(path, original);
            var back = Util.DeserializeFromFile(path) as Dictionary<string, int>;

            Assert.NotNull(back); // null without the package: the call throws and Util swallows it
            Assert.Equal(original, back);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AssetBase_round_trips_through_BinaryFormatter_like_FlotsamAssetCache()
    {
        var asset = new AssetBase(UUID.Random(), "smoke", (sbyte)AssetType.Notecard, UUID.Zero.ToString())
        {
            Data = new byte[] { 1, 2, 3, 4 }
        };
        using var ms = new MemoryStream();
#pragma warning disable SYSLIB0011 // the same calls FlotsamAssetCache makes
        var formatter = new BinaryFormatter();
        formatter.Serialize(ms, asset);
        ms.Position = 0;
        var back = (AssetBase)formatter.Deserialize(ms);
#pragma warning restore SYSLIB0011

        Assert.Equal(asset.FullID, back.FullID);
        Assert.Equal(asset.Data, back.Data);
    }
}
