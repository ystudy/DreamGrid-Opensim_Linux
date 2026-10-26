namespace DreamGrid.Launcher.Core;

/// <summary>Holds <c>&lt;data&gt;/launcher.lock</c> open with no sharing for the process lifetime: one launcher per data directory.</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    /// <summary>Returns the lock, or null when another launcher holds it. The file is never deleted.</summary>
    public static InstanceLock? TryAcquire(string dataDir)
    {
        LauncherPaths.CreateOwnerOnly(dataDir);
        try
        {
            return new InstanceLock(new FileStream(Path.Combine(dataDir, "launcher.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
