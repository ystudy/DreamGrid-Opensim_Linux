namespace DreamGrid.Launcher.Core.Splash;

/// <summary>
/// The built-in splash page (spec 2026-11-01-viewer-splash-design §4.6): the grid name, the live status line and the links,
/// over a dark background. Shown until a design is published and on machines without a guest side, and the editor's
/// starting draft.
/// </summary>
public static class SplashDefaults
{
    public static SplashDesign Design()
    {
        static SplashFont Font(int size, bool bold) => new(size, "#ffffff", bold, SplashAlign.Centre, SplashFontFamily.Sans);
        const string Clear = "#000000";
        return new SplashDesign(1280, 800, new SplashBackground("#1a2a3a", null, 50, 50, 0), false, 16,
        [
            new SplashBlock(1, SplashBlockKind.GridName, 240, 260, 800, 120, 1, Clear, 100, null, false, 0, Font(64, true),
                new SplashContent(ShowName: true)),
            new SplashBlock(2, SplashBlockKind.Status, 340, 400, 600, 60, 2, Clear, 100, null, false, 0, Font(24, false),
                new SplashContent(Status: new SplashStatusLines(true, true, true))),
            new SplashBlock(3, SplashBlockKind.Links, 290, 640, 700, 60, 3, Clear, 100, null, false, 0, Font(20, false),
                new SplashContent(Links: new SplashLinks(SignUp: true, ForgotPassword: true, Terms: true, Privacy: true, FrontPage: false))),
        ]);
    }
}
