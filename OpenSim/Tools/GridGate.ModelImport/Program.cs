using System;
using System.Collections.Generic;
using GridGate.ModelImport.Building;

namespace GridGate.ModelImport;

/// <summary>
/// GridGate.ModelImport check --input &lt;file&gt; --limits &lt;json&gt; --options &lt;json&gt; --out &lt;dir&gt;
/// Exit 0: a report was written (blocking or not). 2: bad arguments or unreadable JSON. 3: the conversion failed (message on
/// stderr, no report).
/// </summary>
public static class Program
{
    private const string Usage = "usage: GridGate.ModelImport check --input <file> --limits <json> --options <json> --out <dir>";
    private static readonly string[] Names = { "--input", "--limits", "--options", "--out" };

    public static int Main(string[] args)
    {
        OpenSim.Framework.Util.InstallNativeDllFallback();

        var values = Parse(args);
        if (values == null)
        {
            Console.Error.WriteLine(Usage);
            return JobWriter.BadArguments;
        }
        return JobWriter.Run(values["--input"], values["--limits"], values["--options"], values["--out"]);
    }

    // "check" followed by each named argument exactly once with a value; anything else is null.
    private static Dictionary<string, string>? Parse(string[] args)
    {
        if (args.Length != 1 + Names.Length * 2 || args[0] != "check") return null;
        var values = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i += 2)
        {
            var name = args[i];
            var value = args[i + 1];
            if (Array.IndexOf(Names, name) < 0 || values.ContainsKey(name) || value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal))
                return null;
            values[name] = value;
        }
        return values;
    }
}
