using System.Xml;
using OpenSim.Framework.Console;
using Xunit;

namespace DreamGrid.Platform.Tests;

/// <summary>
/// The launcher opens Robust's REST console while Robust is still loading its services. Each service adds console
/// commands as it loads, and opening a session reads the whole command tree (Commands.GetXml) on an HTTP thread.
/// Without a lock the read threw "Collection was modified" and the session answered 500.
/// </summary>
public class ConsoleCommandsTests
{
    [Fact]
    public async Task GetXml_while_commands_are_added_on_another_thread_does_not_throw()
    {
        var commands = new Commands();
        commands.AddCommand("Help", false, "help", "help", "help", (_, _) => { });
        commands.AddCommand("General", false, "quit", "quit", "quit", (_, _) => { });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var adder = Task.Run(() =>
        {
            for (int i = 0; !cts.IsCancellationRequested; i++)
                commands.AddCommand("Test", false, $"group{i % 50} command{i}", "x", "x", (_, _) => { });
        });
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                commands.GetXml(new XmlDocument());
                Assert.True(commands.HasCommand("help"));
            }
        });

        await Task.WhenAll(adder, reader);
    }
}
