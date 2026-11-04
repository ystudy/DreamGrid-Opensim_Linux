using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// The takedown cases, one folder each under <paramref name="root"/> (<c>TD-2026-001/case.json</c> and its <c>assets</c>). All of
/// them are read at start and kept in memory; each change is written at once (<see cref="AtomicFile"/>). A case file that can't
/// be read is left where it is, and <see cref="Unreadable"/> stays true until the launcher starts again: the hold then refuses
/// every delete, because a lost case could be one that holds an account. Folders are made 0700 on Linux: only the launcher reads
/// the evidence. Safe to call from any thread.
/// </summary>
public sealed class TakedownStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly ILogger<TakedownStore> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TakedownCase> _cases = new(StringComparer.Ordinal);

    public TakedownStore(string root, TimeProvider time, ILogger<TakedownStore> log)
    {
        _root = root;
        _time = time;
        _log = log;
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root, "TD-*"))
        {
            var file = Path.Combine(dir, "case.json");
            try
            {
                if (!File.Exists(file)) continue;
                if (JsonSerializer.Deserialize<TakedownCase>(File.ReadAllText(file), Options) is { Number.Length: > 0 } c) _cases[c.Number] = c;
                else throw new JsonException("empty");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Unreadable = true;
                log.LogWarning("Takedowns: {Path} could not be read ({Error}); it is left where it is, and no account can be deleted until it is fixed.",
                    file, e.GetType().Name);
            }
        }
    }

    /// <summary>True when a case file couldn't be read at start.</summary>
    public bool Unreadable { get; }

    /// <summary>Every case: open ones first, then the newest number first.</summary>
    public IReadOnlyList<TakedownCase> All()
    {
        lock (_gate)
            return [.. _cases.Values.OrderByDescending(c => c.Open).ThenByDescending(c => Year(c.Number)).ThenByDescending(c => Count(c.Number))];
    }

    public TakedownCase? Get(string number)
    {
        lock (_gate) return _cases.GetValueOrDefault(number);
    }

    /// <summary>The cases that list <paramref name="principal"/> as a person.</summary>
    public IReadOnlyList<TakedownCase> Naming(Guid principal) => [.. All().Where(c => c.People.Any(p => p.Principal == principal))];

    public string CaseFolder(string number) => Path.Combine(_root, number);

    /// <summary>
    /// Gives out the next number (<c>TD-&lt;year&gt;-&lt;count:D3&gt;</c>, the count restarting each UTC year), makes its folder and
    /// saves what <paramref name="build"/> makes of it, all under one lock. Throws <see cref="IOException"/> when it can't be saved.
    /// </summary>
    public TakedownCase Open(TakedownNotice notice, bool tell, Func<string, TakedownCase> build)
    {
        lock (_gate)
        {
            var year = _time.GetUtcNow().Year;
            var prefix = $"TD-{year}-";
            var taken = Directory.Exists(_root)
                ? Directory.GetDirectories(_root, prefix + "*").Select(d => Count(Path.GetFileName(d))).DefaultIfEmpty(0).Max()
                : 0;
            taken = Math.Max(taken, _cases.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(Count).DefaultIfEmpty(0).Max());
            var number = prefix + (taken + 1).ToString("D3", CultureInfo.InvariantCulture);
            var folder = CaseFolder(number);
            Directory.CreateDirectory(Path.Combine(folder, "assets"));
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                File.SetUnixFileMode(_root, Private);
                File.SetUnixFileMode(folder, Private);
                File.SetUnixFileMode(Path.Combine(folder, "assets"), Private);
            }
            var c = build(number) with { Number = number, Notice = notice, TellResident = tell };
            Write(c);
            _cases[number] = c;
            return c;
        }
    }

    /// <summary>Saves a change to a case; false (and the old copy kept in memory) when it couldn't be written.</summary>
    public bool Save(TakedownCase c)
    {
        lock (_gate)
        {
            try
            {
                Write(c);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Takedowns: case {Number} could not be saved ({Error}).", c.Number, e.GetType().Name);
                return false;
            }
            _cases[c.Number] = c;
            return true;
        }
    }

    private void Write(TakedownCase c)
    {
        try
        {
            AtomicFile.WriteAllText(Path.Combine(CaseFolder(c.Number), "case.json"), JsonSerializer.Serialize(c, Options));
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException("The case file could not be written.", e);
        }
    }

    private static int Year(string number) => number.Length >= 7 && int.TryParse(number.AsSpan(3, 4), out var y) ? y : 0;

    private static int Count(string number) =>
        number.LastIndexOf('-') is var dash and > 0 && int.TryParse(number.AsSpan(dash + 1), out var n) ? n : 0;
}
