using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DreamGrid.Launcher.Core.ModelImport;

/// <summary>
/// Job folders for model uploads, <c>&lt;data&gt;/state/uploads/&lt;32 hex&gt;/</c>, always fresh (the converter doesn't clean a reused one),
/// and the one conversion slot for the whole install. A folder's owner and creation time are kept in its <c>owner</c> file.
/// </summary>
public sealed partial class ImportJobs(LauncherPaths paths, TimeProvider time)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private const string OwnerFile = "owner";
    private int _slot;

    [GeneratedRegex(@"^[0-9a-f]{32}\z")]
    private static partial Regex JobId();

    /// <summary>32 lowercase hex digits and nothing else (not even a trailing newline, which <c>$</c> would let through).</summary>
    internal static bool IsJobId(string id) => JobId().IsMatch(id);

    /// <summary>Makes a new folder for <paramref name="guest"/> and returns its path.</summary>
    public string Create(Guid guest)
    {
        var dir = Path.Combine(paths.Uploads, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, OwnerFile),
            guest.ToString("D") + "\n" + time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        return dir;
    }

    /// <summary>True when the caller got the slot; it must call <see cref="LeaveSlot"/>.</summary>
    public bool TryEnterSlot() => Interlocked.CompareExchange(ref _slot, 1, 0) == 0;

    public void LeaveSlot() => Interlocked.Exchange(ref _slot, 0);

    public bool Owns(Guid guest, string jobId)
    {
        if (!IsJobId(jobId)) return false;
        try
        {
            var lines = File.ReadAllLines(Path.Combine(paths.Uploads, jobId, OwnerFile));
            return lines.Length > 0 && Guid.TryParse(lines[0], out var owner) && owner == guest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The ids of every job folder whose owner file names <paramref name="guest"/>.</summary>
    public IReadOnlyList<string> JobsOf(Guid guest)
    {
        if (!Directory.Exists(paths.Uploads)) return [];
        return Directory.EnumerateDirectories(paths.Uploads).Select(Path.GetFileName).OfType<string>()
            .Where(id => Owns(guest, id)).ToList();
    }

    public void Delete(string jobId)
    {
        if (!IsJobId(jobId)) return;
        TryDelete(Path.Combine(paths.Uploads, jobId));
    }

    /// <summary>Deletes folders older than <see cref="MaxAge"/> (a folder with no readable owner file goes by its own creation time).</summary>
    public void Sweep()
    {
        if (!Directory.Exists(paths.Uploads)) return;
        var now = time.GetUtcNow();
        foreach (var dir in Directory.EnumerateDirectories(paths.Uploads))
            if (IsJobId(Path.GetFileName(dir)) && now - CreatedAt(dir) > MaxAge)
                TryDelete(dir);
    }

    private static DateTimeOffset CreatedAt(string dir)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(dir, OwnerFile));
            if (lines.Length > 1 && DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                return at;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new DateTimeOffset(Directory.GetCreationTimeUtc(dir), TimeSpan.Zero);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
