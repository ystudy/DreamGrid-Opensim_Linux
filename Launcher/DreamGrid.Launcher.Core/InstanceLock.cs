namespace DreamGrid.Launcher.Core;

/// <summary>Holds <c>&lt;data&gt;/launcher.lock</c> open with no sharing for the process lifetime: one launcher per data directory.</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Returns the lock, or null when another launcher holds it. The file is never deleted. A data directory another
    /// launcher holds is left as it is: its owner-only permissions are applied only once the lock is ours (a missing
    /// directory is created owner-only first, since the lock file goes in it).
    /// </summary>
    public static InstanceLock? TryAcquire(string dataDir)
    {
        var existed = Directory.Exists(dataDir);
        if (!existed) LauncherPaths.CreateOwnerOnly(dataDir);
        FileStream stream;
        try
        {
            stream = Open(dataDir);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (existed)
        {
            // A directory this account cannot use yet: narrowing it says why (or, for an administrator, fixes it).
            LauncherPaths.CreateOwnerOnly(dataDir);
            try { stream = Open(dataDir); }
            catch (IOException) { return null; }
            return new InstanceLock(stream);
        }
        if (existed)
        {
            try { LauncherPaths.CreateOwnerOnly(dataDir); }
            catch { stream.Dispose(); throw; }
        }
        return new InstanceLock(stream);
    }

    private static FileStream Open(string dataDir) =>
        new(Path.Combine(dataDir, "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public void Dispose() => _stream.Dispose();
}
