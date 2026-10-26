using DreamGrid.Launcher.Core;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public class InstanceLockTests
{
    [Fact]
    public void Second_lock_on_same_dir_is_refused_until_first_is_disposed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dgtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = InstanceLock.TryAcquire(dir);
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(dir));

            first.Dispose();

            using var again = InstanceLock.TryAcquire(dir);
            Assert.NotNull(again);
            Assert.True(File.Exists(Path.Combine(dir, "launcher.lock")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
