using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Config;

/// <summary>
/// The website's address as viewers and residents are given it: Robust's grid info (welcome, register, password) and the guest
/// Connect page's grid manager settings both use this, so they can't disagree.
/// </summary>
public static class WebsiteAddress
{
    /// <summary>"http://host:port", the host as <see cref="HostAddress.Effective"/> gives it. No trailing slash.</summary>
    public static string Base(LauncherSettings s) => $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}";

    public static string Welcome(LauncherSettings s) => Base(s) + "/welcome";
    public static string SignUp(LauncherSettings s) => Base(s) + "/signup";
    public static string ForgotPassword(LauncherSettings s) => Base(s) + "/guest/forgot-password";
}
