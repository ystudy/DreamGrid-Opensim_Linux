using System.Net;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class GridConsoleCommandsTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task Create_user_answers_a_real_prompt_line_with_an_empty_line()
    {
        var console = new FakeRestConsole(cmd => cmd.StartsWith("create user") ? [("Model name []: ", true)] : []);

        await GridConsoleCommands.CreateUserAsync(console.Client, "Test", "Owner", "pw1", null, Guid.Empty, Settle, CancellationToken.None);

        Assert.Equal(2, console.Commands.Count);
        Assert.Equal("", console.Commands[1]);
    }

    [Fact]
    public async Task Create_user_does_not_answer_ordinary_output_that_ends_in_a_colon()
    {
        var console = new FakeRestConsole(cmd => cmd.StartsWith("create user") ? [("Account details:", false)] : []);

        await GridConsoleCommands.CreateUserAsync(console.Client, "Test", "Owner", "pw1", null, Guid.Empty, Settle, CancellationToken.None);

        var only = Assert.Single(console.Commands);
        Assert.Equal($"create user Test Owner pw1 \"\" {Guid.Empty} \"\"", only);
    }

    [Fact]
    public async Task Create_user_sends_the_model_quoted()
    {
        var console = new FakeRestConsole(_ => []);
        await GridConsoleCommands.CreateUserAsync(console.Client, "Ann", "Example", "pw1", null, Guid.Empty, Settle, CancellationToken.None,
            "Starter01 Avatar");
        Assert.Equal($"create user Ann Example pw1 \"\" {Guid.Empty} \"Starter01 Avatar\"", Assert.Single(console.Commands));
    }

    [Theory]
    [InlineData("Starter01 Big Avatar")]
    [InlineData("Starter01")]
    [InlineData("Starter01 Ava\"tar")]
    public async Task A_model_with_three_words_is_refused(string model)
    {
        var console = new FakeRestConsole(_ => []);
        await Assert.ThrowsAsync<ArgumentException>(() => GridConsoleCommands.CreateUserAsync(console.Client, "Ann", "Example", "pw1", null,
            Guid.Empty, Settle, CancellationToken.None, model));
        Assert.Empty(console.Commands);
    }

    [Theory]
    [InlineData("Logins are enabled for region Welcome", true)]
    [InlineData("Logins are disabled for region Welcome", false)]
    // What AccessModule really answers to "login status" (the lines above are its replies to enable/disable).
    [InlineData("Login in Welcome are enabled", true)]
    [InlineData("Login in Welcome are disabled", false)]
    [InlineData("Login in My Region are enabled", true)]
    public async Task Login_status_is_read_from_the_region_console(string reply, bool expected)
    {
        var console = new FakeRestConsole(cmd => cmd == "login status" ? [(reply, false)] : []);

        Assert.Equal(expected, await GridConsoleCommands.LoginsEnabledAsync(console.Client, Settle, CancellationToken.None));
    }

    [Fact]
    public async Task Login_status_is_null_when_the_reply_is_not_recognised()
    {
        var console = new FakeRestConsole(_ => [("something else", false)]);

        Assert.Null(await GridConsoleCommands.LoginsEnabledAsync(console.Client, Settle, CancellationToken.None));
    }

    [Theory]
    [InlineData("max/min/avg/sum: 26.3/4.8/12.5/819200", true)]   // island
    [InlineData("max/min/avg/sum: 21/21/21/1376256", false)]       // flat default terrain: the load did not happen
    [InlineData("max/min/avg/sum: 26,3/4,8/12,5/819200", true)]   // comma decimal culture
    public async Task Island_terrain_is_verified_from_terrain_stats(string stats, bool expected)
    {
        var console = new FakeRestConsole(cmd => cmd == "terrain stats" ? [("Channel 256x256", false), (stats, false)] : []);

        var time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(10) };

        Assert.Equal(expected, await GridConsoleCommands.IslandTerrainLoadedAsync(console.Client, Settle, time, CancellationToken.None));
    }

    [Fact]
    public async Task Island_terrain_check_asks_again_when_the_first_stats_reply_has_no_stats_line()
    {
        // The console thread can still be finishing "terrain load" when the first "terrain stats" is sent.
        var asked = 0;
        var console = new FakeRestConsole(cmd => cmd == "terrain stats"
            ? (++asked == 1 ? [("Terrain load in progress", false)] : [("max/min/avg/sum: 26.3/4.8/12.5/819200", false)])
            : []);

        Assert.True(await GridConsoleCommands.IslandTerrainLoadedAsync(console.Client, Settle, TimeProvider.System, CancellationToken.None));
        Assert.Equal(2, console.Commands.Count(c => c == "terrain stats"));
    }

    [Fact]
    public async Task Island_terrain_check_asks_again_while_stats_still_show_the_flat_land_from_before_the_load()
    {
        // CI saw this: "terrain stats" answered with the "terrain fill 0" land, and the region logged the island
        // loaded a moment later.
        var asked = 0;
        var console = new FakeRestConsole(cmd => cmd == "terrain stats"
            ? (++asked == 1 ? [("max/min/avg/sum: 0/0/0/0", false)] : [("max/min/avg/sum: 26.3/4.8/12.5/819200", false)])
            : []);

        Assert.True(await GridConsoleCommands.IslandTerrainLoadedAsync(console.Client, Settle, TimeProvider.System, CancellationToken.None));
        Assert.Equal(2, console.Commands.Count(c => c == "terrain stats"));
    }

    [Fact]
    public async Task Island_terrain_check_is_false_when_the_land_stays_flat_until_the_wait_runs_out()
    {
        var time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(10) };
        var console = new FakeRestConsole(cmd => cmd == "terrain stats" ? [("max/min/avg/sum: 21/21/21/1376256", false)] : []);

        Assert.False(await GridConsoleCommands.IslandTerrainLoadedAsync(console.Client, Settle, time, CancellationToken.None));
        Assert.True(console.Commands.Count(c => c == "terrain stats") >= 2);
    }

    [Fact]
    public async Task Island_terrain_check_gives_up_when_no_stats_line_ever_arrives()
    {
        var time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(10) };
        var console = new FakeRestConsole(_ => [("nothing useful", false)]);

        Assert.False(await GridConsoleCommands.IslandTerrainLoadedAsync(console.Client, Settle, time, CancellationToken.None));
        Assert.True(console.Commands.Count(c => c == "terrain stats") >= 2);
    }

    [Theory]
    [InlineData("--pass")]
    [InlineData("--x=")]
    [InlineData("pass word")]
    [InlineData("pass\"word")]
    [InlineData("")]
    public void Owner_passwords_the_console_would_mangle_are_rejected(string password)
    {
        Assert.Throws<ArgumentException>(() => GridConsoleCommands.ValidateOwner("Test", "Owner", password, null));
    }

    [Fact]
    public void A_password_with_dashes_inside_is_accepted()
    {
        GridConsoleCommands.ValidateOwner("Test", "Owner", "pass-word1", null);
    }
}
