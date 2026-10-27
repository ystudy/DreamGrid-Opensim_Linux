using DreamGrid.Launcher.Core.Grid;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class GridConsoleCommandsRegionsTests
{
    // OpenSim's "show regions" (OpenSim.cs, ConsoleDisplayTable): names pad to 20 and can hold spaces.
    private static readonly string[] ShowRegions =
    [
        "Name                  ID                                    Position     Size         Port   Ready?  Estate              ",
        "Welcome               6f1c1b8e-2f1a-4c55-9d3e-0a7b2c4d5e6f  1000,1000    256x256      8004   Yes     My World Estate     ",
        "Sunset Shore Long Name  0b0c3a1e-5d2f-4e6a-8b7c-9d0e1f2a3b4c  1001,1000    256x256      8005   No      My World Estate     ",
    ];

    [Fact]
    public void Show_regions_output_is_parsed()
    {
        Assert.Equal(
            [Guid.Parse("6f1c1b8e-2f1a-4c55-9d3e-0a7b2c4d5e6f"), Guid.Parse("0b0c3a1e-5d2f-4e6a-8b7c-9d0e1f2a3b4c")],
            GridConsoleCommands.ParseShowRegions(ShowRegions));
    }

    [Fact]
    public void Output_without_a_table_is_unknown()
    {
        Assert.Null(GridConsoleCommands.ParseShowRegions([]));
        Assert.Null(GridConsoleCommands.ParseShowRegions(["Unknown command"]));
    }

    [Fact]
    public void Login_status_lines_are_read_per_region()
    {
        var status = GridConsoleCommands.ParseLoginStatus(
            ["Login in Welcome are enabled", "Login in Sunset Shore are disabled", "something else"]);

        Assert.Equal(new Dictionary<string, bool> { ["Welcome"] = true, ["Sunset Shore"] = false }, status);
    }
}
