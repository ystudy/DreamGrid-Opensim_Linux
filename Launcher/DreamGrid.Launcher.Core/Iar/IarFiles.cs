using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Iar;

/// <summary>
/// A resident's finished backup: whose, the file's name in <see cref="LauncherPaths.IarsReady"/> (a random id), the download token,
/// the guest session stamp it was made for, when the link expires, what was left out, and the item count and size.
/// </summary>
public sealed record IarReadyFile(Guid Owner, string File, string Token, string Stamp, DateTimeOffset Expires, IReadOnlyList<LeftOutItem> LeftOut,
    int Items, long Bytes);

/// <summary>
/// Residents' finished backups and their download links (spec 2026-11-11-iar-backups-design §5.2). One file per resident: a new
/// backup replaces the old one. A link works only for its owner, with the session stamp it was made for, until it expires; the
/// token is 32 random bytes and is compared in constant time. Kept in <c>&lt;guests&gt;/iar-ready.json</c>. <see cref="Sweep"/>
/// deletes expired files and any file nothing points at, and never touches staff files. Safe to call from any thread.
/// </summary>
public sealed class IarFiles
{
    private readonly LauncherPaths _paths;
    private readonly TimeProvider _time;
    private readonly JsonStateFile<List<IarReadyFile>> _file;
    private readonly Lock _gate = new();
    private readonly List<IarReadyFile> _ready;

    public IarFiles(LauncherPaths paths, TimeProvider time, ILogger<IarFiles>? log = null)
    {
        _paths = paths;
        _time = time;
        _file = new JsonStateFile<List<IarReadyFile>>(Path.Combine(paths.Guests, "iar-ready.json"), () => [], log);
        _ready = _file.Load();
    }

    /// <summary>The full path of a ready file.</summary>
    public string PathOf(IarReadyFile r) => Path.Combine(_paths.IarsReady, r.File);

    /// <summary>Moves <paramref name="staged"/> into the ready folder for <paramref name="owner"/>, replacing their previous one.</summary>
    public IarReadyFile Publish(Guid owner, string stamp, string staged, TimeSpan life, IReadOnlyList<LeftOutItem> leftOut, int items)
    {
        Directory.CreateDirectory(_paths.IarsReady);
        var name = Guid.NewGuid().ToString("N") + ".iar";
        var bytes = new FileInfo(staged).Length;
        File.Move(staged, Path.Combine(_paths.IarsReady, name));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var ready = new IarReadyFile(owner, name, token, stamp, _time.GetUtcNow() + life, leftOut, items, bytes);
        lock (_gate)
        {
            foreach (var old in _ready.Where(r => r.Owner == owner).ToList())
            {
                Delete(old);
                _ready.Remove(old);
            }
            _ready.Add(ready);
            _file.Save(_ready);
        }
        return ready;
    }

    /// <summary>The owner's backup while its link works and its file is there; otherwise null.</summary>
    public IarReadyFile? Find(Guid owner)
    {
        lock (_gate) return _ready.FirstOrDefault(r => r.Owner == owner && Live(r));
    }

    /// <summary>The backup <paramref name="token"/> names, when it belongs to <paramref name="principal"/> in this session; otherwise null.</summary>
    public IarReadyFile? ForDownload(string token, Guid principal, string stamp)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var wanted = Encoding.UTF8.GetBytes(token);
        lock (_gate)
        {
            var r = _ready.FirstOrDefault(x => x.Owner == principal);
            if (r is null || !Live(r)) return null;
            var same = CryptographicOperations.FixedTimeEquals(wanted, Encoding.UTF8.GetBytes(r.Token));
            return same && r.Stamp == stamp ? r : null;
        }
    }

    /// <summary>Deletes expired backups, and any file in the ready folder that no backup points at.</summary>
    public void Sweep()
    {
        lock (_gate)
        {
            var expired = _ready.Where(r => !Live(r)).ToList();
            foreach (var r in expired)
            {
                Delete(r);
                _ready.Remove(r);
            }
            if (expired.Count > 0) _file.Save(_ready);
            if (!Directory.Exists(_paths.IarsReady)) return;
            var known = _ready.Select(r => r.File).ToHashSet(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(_paths.IarsReady))
                if (!known.Contains(Path.GetFileName(path)))
                    TryDelete(path);
        }
    }

    private bool Live(IarReadyFile r) => r.Expires > _time.GetUtcNow() && File.Exists(PathOf(r));

    private void Delete(IarReadyFile r) => TryDelete(PathOf(r));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
