using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DreamGrid.Launcher.Core.Processes;

// Modes:
//   --crash                       exit with code 3 after 200 ms
//   --serve <port>                answer 200 on /health, exit on /quit, otherwise run until killed;
//                                 every request writes one line to stdout and one to stderr
//   --launch <port> <pidfile>     start "--serve <port>" through ManagedProcess (as the launcher would), then exit at once
if (args.Length == 0) return 2;

switch (args[0])
{
    case "--crash":
        await Task.Delay(200);
        return 3;

    case "--serve" when args.Length >= 2:
        await Serve(int.Parse(args[1]));
        return 0;

    case "--launch" when args.Length >= 3:
        await Launch(int.Parse(args[1]), args[2]);
        return 0;

    default:
        return 2;
}

static async Task Serve(int port)
{
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer);
        var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];
        var quit = requestLine.Contains("/quit", StringComparison.Ordinal);

        // Deliberately unguarded: after the process that started us has gone these pipes are broken,
        // and this proves a child survives that (spec 4.12).
        Console.Out.WriteLine($"stdout: {requestLine}");
        Console.Error.WriteLine($"stderr: {requestLine}");

        var body = Encoding.ASCII.GetBytes(quit ? "bye" : "ok");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
        if (quit) return;
    }
}

static async Task Launch(int port, string pidFile)
{
    var self = Environment.ProcessPath!;
    var spec = new ProcessSpec(
        "fakechild",
        () =>
        {
            var psi = new ProcessStartInfo(self);
            psi.ArgumentList.Add("--serve");
            psi.ArgumentList.Add(port.ToString());
            return psi;
        },
        pidFile,
        async ct =>
        {
            try
            {
                using var http = new HttpClient();
                return (await http.GetAsync($"http://127.0.0.1:{port}/health", ct)).IsSuccessStatusCode;
            }
            catch (HttpRequestException) { return false; }
        },
        _ => Task.CompletedTask,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5),
        "DreamGrid.FakeChild");
    var process = new ManagedProcess(spec, new RestartPolicy(), TimeProvider.System);
    await process.StartAsync(CancellationToken.None);
    if (process.State != ProcessState.Running) Environment.Exit(4);
}
