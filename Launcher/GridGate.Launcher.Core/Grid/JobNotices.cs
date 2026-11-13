namespace GridGate.Launcher.Core.Grid;

/// <summary>The plain words shown while a grid job is running.</summary>
public static class JobNotices
{
    /// <summary>"&lt;what&gt; is still running. Try again when it finishes."</summary>
    public static string Busy(string what) => $"{what} is still running. Try again when it finishes.";
}
