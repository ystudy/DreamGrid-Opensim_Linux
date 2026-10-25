using System.Net;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class GridConsoleCommandsTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Models OpenSim's RemoteConsole: each COMMAND queues the lines <c>respond</c> returns for it (plus the echo
    /// and the command prompt the real server sends), and the next /ReadResponses/ hands them out.
    /// </summary>
    private sealed class FakeConsole
    {
        private readonly Queue<(string Text, bool Prompt, bool Command, bool Input)> _pending = new();
        public List<string> Commands { get; } = new();
        public RestConsoleClient Client { get; }

        public FakeConsole(Func<string, (string Text, bool Prompt)[]> respond)
        {
            var handler = new StubHandler(r =>
            {
                lock (_pending)
                {
                    switch (r.Uri.AbsolutePath)
                    {
                        case "/StartSession/":
                            return (HttpStatusCode.OK, "<ConsoleSession><SessionID>sid</SessionID><Prompt>R# </Prompt></ConsoleSession>");
                        case "/SessionCommand/":
                            var cmd = r.Form("COMMAND");
                            Commands.Add(cmd);
                            _pending.Enqueue((cmd, false, false, true));
                            foreach (var (text, prompt) in respond(cmd)) _pending.Enqueue((text, prompt, false, false));
                            _pending.Enqueue(("R# ", true, true, false));
                            return (HttpStatusCode.OK, "<ConsoleSession><Result>OK</Result></ConsoleSession>");
                        case "/CloseSession/":
                            return (HttpStatusCode.OK, "<ConsoleSession/>");
                        default:
                            var lines = new List<(string, bool, bool, bool)>();
                            while (_pending.Count > 0) lines.Add(_pending.Dequeue());
                            return (HttpStatusCode.OK, "<ConsoleSession>" + string.Concat(lines.Select(l =>
                                $"<Line Number=\"1\" Level=\"normal\" Prompt=\"{(l.Item2 ? "true" : "false")}\" Command=\"{(l.Item3 ? "true" : "false")}\" Input=\"{(l.Item4 ? "true" : "false")}\">{System.Security.SecurityElement.Escape(l.Item1)}</Line>"))
                                + "</ConsoleSession>");
                    }
                }
            });
            Client = new RestConsoleClient(new HttpClient(handler), new Uri("http://127.0.0.1:8010/"), "u", "p");
        }
    }

    [Fact]
    public async Task Create_user_answers_a_real_prompt_line_with_an_empty_line()
    {
        var console = new FakeConsole(cmd => cmd.StartsWith("create user") ? [("Model name []: ", true)] : []);

        await GridConsoleCommands.CreateUserAsync(console.Client, "Test", "Owner", "pw1", null, Guid.Empty, Settle, CancellationToken.None);

        Assert.Equal(2, console.Commands.Count);
        Assert.Equal("", console.Commands[1]);
    }

    [Fact]
    public async Task Create_user_does_not_answer_ordinary_output_that_ends_in_a_colon()
    {
        var console = new FakeConsole(cmd => cmd.StartsWith("create user") ? [("Account details:", false)] : []);

        await GridConsoleCommands.CreateUserAsync(console.Client, "Test", "Owner", "pw1", null, Guid.Empty, Settle, CancellationToken.None);

        var only = Assert.Single(console.Commands);
        Assert.Equal($"create user Test Owner pw1 \"\" {Guid.Empty} \"\"", only);
    }

    [Theory]
    [InlineData("Logins are enabled for region Welcome", true)]
    [InlineData("Logins are disabled for region Welcome", false)]
    public async Task Login_status_is_read_from_the_region_console(string reply, bool expected)
    {
        var console = new FakeConsole(cmd => cmd == "login status" ? [(reply, false)] : []);

        Assert.Equal(expected, await GridConsoleCommands.LoginsEnabledAsync(console.Client, Settle, CancellationToken.None));
    }

    [Fact]
    public async Task Login_status_is_null_when_the_reply_is_not_recognised()
    {
        var console = new FakeConsole(_ => [("something else", false)]);

        Assert.Null(await GridConsoleCommands.LoginsEnabledAsync(console.Client, Settle, CancellationToken.None));
    }

    [Theory]
    [InlineData("max/min/avg/sum: 26.3/4.8/12.5/819200", true)]   // island
    [InlineData("max/min/avg/sum: 21/21/21/1376256", false)]       // flat default terrain: the load did not happen
    [InlineData("max/min/avg/sum: 26,3/4,8/12,5/819200", true)]   // comma decimal culture
    public async Task Island_terrain_is_verified_from_terrain_stats(string stats, bool expected)
    {
        var console = new FakeConsole(cmd => cmd == "terrain stats" ? [("Channel 256x256", false), (stats, false)] : []);

        Assert.Equal(expected, await GridConsoleCommands.IslandTerrainLoadedAsync(console.Client, Settle, CancellationToken.None));
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
