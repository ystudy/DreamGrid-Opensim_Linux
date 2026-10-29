using System.Text.Json.Serialization;

namespace DreamGrid.Launcher.Core.Settings;

/// <summary>
/// The kind of grid the owner chose at first use; saved by name. A null plan in the settings means none chosen yet.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OwnerPlan>))]
public enum OwnerPlan { Local, SimplePublic, GridAsset, RegionServer, Connected }
