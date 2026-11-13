using GridGate.Launcher.Core.Grid;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

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
    public async Task Loaded_regions_ask_again_when_the_first_show_regions_reply_has_no_table()
    {
        // A slow console answered after the settle time: the launcher that adopted the program must not take
        // "unknown" for good, or "Start everything" never loads a region the program is missing.
        var asked = 0;
        var console = new FakeRestConsole(cmd => cmd == "show regions"
            ? (++asked == 1 ? [] : ShowRegions.Select(l => (l, false)).ToArray())
            : []);

        var ids = await GridConsoleCommands.LoadedRegionsAsync(console.Client, Settle, TimeProvider.System, CancellationToken.None);

        Assert.Equal(2, ids?.Count);
        Assert.Equal(2, console.Commands.Count(c => c == "show regions"));
    }

    [Fact]
    public async Task Loaded_regions_are_unknown_when_no_table_arrives_before_the_wait_runs_out()
    {
        var time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(10) };
        var console = new FakeRestConsole(_ => [("Unknown command", false)]);

        Assert.Null(await GridConsoleCommands.LoadedRegionsAsync(console.Client, Settle, time, CancellationToken.None));
        Assert.True(console.Commands.Count(c => c == "show regions") >= 2);
    }

    [Fact]
    public async Task Adopted_login_status_asks_again_when_the_first_reply_has_no_answer()
    {
        // A slow console answered after the settle time. The region's one-time alert went out before this launcher was
        // listening, so "no answer" taken for "disabled" left an adopted region Starting for good (CI, Debian 13).
        var asked = 0;
        var console = new FakeRestConsole(cmd => cmd == "login status"
            ? (++asked == 1 ? [] : [("Login in Isle are enabled", false)])
            : []);

        var lines = await GridConsoleCommands.AdoptedLoginStatusAsync(console.Client, Settle, TimeProvider.System, CancellationToken.None);

        Assert.True(GridConsoleCommands.ParseLoginStatus(lines)["Isle"]);
        Assert.Equal(2, console.Commands.Count(c => c == "login status"));
    }

    [Fact]
    public async Task Adopted_login_status_takes_a_disabled_answer_at_once()
    {
        var console = new FakeRestConsole(cmd => cmd == "login status" ? [("Login in Isle are disabled", false)] : []);

        var lines = await GridConsoleCommands.AdoptedLoginStatusAsync(console.Client, Settle, TimeProvider.System, CancellationToken.None);

        Assert.False(GridConsoleCommands.ParseLoginStatus(lines)["Isle"]);
        Assert.Single(console.Commands, c => c == "login status");
    }

    [Fact]
    public async Task Adopted_login_status_gives_up_when_no_answer_arrives_before_the_wait_runs_out()
    {
        var time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(10) };
        var console = new FakeRestConsole(_ => []);

        Assert.Empty(await GridConsoleCommands.AdoptedLoginStatusAsync(console.Client, Settle, time, CancellationToken.None));
        Assert.True(console.Commands.Count(c => c == "login status") >= 2);
    }

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void Login_status_lines_are_read_per_region()
    {
        var status = GridConsoleCommands.ParseLoginStatus(
            ["Login in Welcome are enabled", "Login in Sunset Shore are disabled", "something else"]);

        Assert.Equal(new Dictionary<string, bool> { ["Welcome"] = true, ["Sunset Shore"] = false }, status);
    }
}
