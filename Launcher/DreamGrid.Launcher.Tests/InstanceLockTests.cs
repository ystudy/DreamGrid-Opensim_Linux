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
    [Fact]
    public void A_refused_lock_leaves_the_directory_permissions_alone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dgtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = InstanceLock.TryAcquire(dir);
            Assert.NotNull(first);
            // Widen the permissions the first lock applied: a refused second lock must not narrow them again.
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(dir);
                var security = info.GetAccessControl();
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                    System.Security.AccessControl.FileSystemRights.ReadAndExecute, System.Security.AccessControl.AccessControlType.Allow));
                info.SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(dir, File.GetUnixFileMode(dir) | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }
            var before = Permissions(dir);

            Assert.Null(InstanceLock.TryAcquire(dir));

            Assert.Equal(before, Permissions(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static string Permissions(string dir)
    {
        if (OperatingSystem.IsWindows())
            return new DirectoryInfo(dir).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
        return File.GetUnixFileMode(dir).ToString();
    }
}
