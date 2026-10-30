using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class PlanPartsTests
{
    [Theory]
    [InlineData(OwnerPlan.Local)]
    [InlineData(OwnerPlan.SimplePublic)]
    [InlineData(OwnerPlan.GridAsset)]
    public void Nothing_missing_returns_null(OwnerPlan plan) =>
        Assert.Null(PlanParts.Missing(plan, new MariaDbBinaries("mariadbd", "mariadb-install-db", null)));

    [Fact]
    public void Missing_mariadb_names_the_part_and_the_installer()
    {
        var maria = MariaDbBinaries.NotFound(string.Join(Environment.NewLine,
            "MariaDB server not found; searched: C:\\DreamGrid\\mariadb\\bin\\mariadbd.exe",
            "MariaDB install program not found; searched: C:\\DreamGrid\\mariadb\\bin\\mariadb-install-db.exe",
            "Pass --mariadb-bin <dir> or set DREAMGRID_MARIADB_BIN."));

        var text = PlanParts.Missing(OwnerPlan.GridAsset, maria);

        Assert.NotNull(text);
        Assert.Contains("Grid Asset Server", text);
        Assert.Contains("the database, where avatars and inventory are kept", text);
        Assert.Contains("MariaDB server not found; searched: C:\\DreamGrid\\mariadb\\bin\\mariadbd.exe", text);
        Assert.DoesNotContain("MariaDB install program", text);
        Assert.DoesNotContain("--mariadb-bin", text);
        Assert.Contains("administrator", text);
        Assert.EndsWith("Run the DreamGrid installer again: it puts the missing part back and keeps your settings and world.", text);
    }
}
