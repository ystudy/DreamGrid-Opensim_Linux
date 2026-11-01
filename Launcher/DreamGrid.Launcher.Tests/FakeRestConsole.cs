using System.Net;
using DreamGrid.Launcher.Core.OpenSim;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// Models OpenSim's RemoteConsole: each COMMAND queues the lines <c>respond</c> returns for it (plus the echo
/// and the command prompt the real server sends), and the next /ReadResponses/ hands them out.
/// </summary>
internal sealed class FakeRestConsole
{
    private readonly Queue<(string Text, bool Prompt, bool Command, bool Input)> _pending = new();
    public List<string> Commands { get; } = new();
    public RestConsoleClient Client { get; }

    public FakeRestConsole(Func<string, (string Text, bool Prompt)[]> respond)
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
