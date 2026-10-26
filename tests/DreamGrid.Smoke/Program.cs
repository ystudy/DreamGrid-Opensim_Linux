using DreamGrid.Smoke;

// Usage: dotnet run --project tests/DreamGrid.Smoke -c Release -- --bin <path-to-bin>
//            [--physics ubODE|BulletSim] [--culture <name>] [--timeout <seconds>] [--keep]
// Exit code 0 when every check passes, 1 otherwise.

string? bin = null, culture = null;
string physics = "ubODE";
int timeout = 300;
bool keep = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--bin": bin = args[++i]; break;
        case "--physics": physics = args[++i]; break;
        case "--culture": culture = args[++i]; break;
        case "--timeout": timeout = int.Parse(args[++i]); break;
        case "--keep": keep = true; break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 2;
    }
}

if (bin is null || !File.Exists(Path.Combine(bin, "OpenSim.dll")))
{
    Console.Error.WriteLine("--bin must point at a built bin/ directory containing OpenSim.dll");
    return 2;
}

var run = new SmokeRun(new SmokeOptions(Path.GetFullPath(bin), physics, culture, timeout, keep));
Console.WriteLine($"sandbox: {run.SandboxBin} (physics {physics}{(culture is null ? "" : ", culture " + culture)})");

var results = await run.RunAsync();
foreach (var r in results)
    Console.WriteLine($"{(r.Passed ? "PASS" : "FAIL")} {r.Id} {r.Detail}");

bool allPassed = results.All(r => r.Passed);
if (!allPassed)
    Console.WriteLine($"log: {Path.Combine(run.SandboxBin, "smoke.log")}");
else if (!keep)
    Directory.Delete(Path.GetDirectoryName(run.SandboxBin)!, recursive: true);

return allPassed ? 0 : 1;
