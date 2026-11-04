using DreamGrid.Launcher.Core.Database;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>InventoryStore's pure helpers: id columns, creator names and folder paths.</summary>
public sealed class InventoryStoreTests
{
    [Fact]
    public void Id_columns_read_as_guid_or_text()
    {
        var id = Guid.NewGuid();
        // MySqlConnector hands char(36) back as a Guid by default; other set-ups give text.
        Assert.Equal(id.ToString("D"), InventoryStore.IdText(id));
        Assert.Equal(id.ToString("D"), InventoryStore.IdText(id.ToString("D")));
        Assert.Equal(id, InventoryStore.IdOf(id));
        Assert.Equal(id, InventoryStore.IdOf(id.ToString().ToUpperInvariant()));
        Assert.Equal(Guid.Empty, InventoryStore.IdOf(DBNull.Value));
        Assert.Null(InventoryStore.IdText(DBNull.Value));
    }
}
