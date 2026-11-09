using System.Net;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Navigation;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Every button shows an icon chosen from its words (ButtonIconTagHelper), and each pick is in the sprite.</summary>
public sealed class ButtonIconsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Save", "save")]
    [InlineData("Save and continue", "save")]
    [InlineData("Save as OAR first", "download")]
    [InlineData("Cancel", "x")]
    [InlineData("Delete group", "trash")]
    [InlineData("Yes, delete forever", "trash")]
    [InlineData("Yes, import", "upload")]
    [InlineData("Yes", "check")]
    [InlineData("Sign in", "sign-in")]
    [InlineData("Sign out everywhere", "sign-out")]
    [InlineData("  Turn\n   off ", "power")]
    [InlineData("Up", "arrow-up")]
    [InlineData("Upload", "upload")]           // "up" only matches the whole word
    [InlineData("Stop being the default", "star")]
    [InlineData("Stop all", "stop")]
    [InlineData("Unsuspend", "play")]
    [InlineData("Suspend", "pause")]
    [InlineData("Send back", "arrow-left")]
    [InlineData("Send my ticket", "send")]
    [InlineData("Let it go", "x")]
    [InlineData("Let Welcome run on its own", "play")]
    [InlineData("Power", "power")]
    [InlineData("Something nobody wrote a rule for", ButtonIcons.Fallback)]
    public void Icon_comes_from_the_first_words(string label, string icon) => Assert.Equal(icon, ButtonIcons.For(label));

    [Fact]
    public void A_button_with_no_words_gets_no_icon() => Assert.Null(ButtonIcons.For("  \n "));

    [Theory]
    [InlineData("btn btn-primary", true)]
    [InlineData("approve btn btn-sm btn-success", true)]
    [InlineData("btn btn-link p-0", false)]
    [InlineData("btn-close", false)]
    [InlineData("nav-link dropdown-toggle", false)]
    public void Only_bootstrap_buttons_with_words_count(string classes, bool button) => Assert.Equal(button, ButtonIcons.IsButton(classes));

    [Fact]
    public async Task Every_icon_a_rule_can_pick_is_in_the_served_sprite()
    {
        await using var f = new LauncherFactory();
        using var r = await f.CreateClient().GetAsync(ButtonIcons.Sprite, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var sprite = XDocument.Parse(await r.Content.ReadAsStringAsync(Ct));
        var ids = sprite.Descendants().Where(e => e.Name.LocalName == "symbol").Select(e => (string?)e.Attribute("id")).ToHashSet();
        var labels = new[]
        {
            "Save", "Save as OAR", "Save size", "Cancel", "Delete", "Search", "Show it on the map", "Show", "Continue", "Back", "Up",
            "Down", "Move", "Copy", "Power", "Turn on", "Turn off", "Suspend", "Stop", "Restart", "Undo", "Redo", "Send", "Join",
            "Add", "Import", "Download", "Change password", "Rename", "Make admin", "Make guest", "Make the land again",
            "Make this the default group", "Use this level", "Mark urgent", "OAR", "Approve", "Sign in", "Sign out", "Yes", "Zzz",
        };
        foreach (var label in labels) Assert.Contains(ButtonIcons.For(label), ids);
        // Named by pages and scripts: the help icon, the splash shelf, its layer buttons, and the editor script's buttons.
        foreach (var name in new[] { "help", "bring-forward", "send-back", "edit", "eye", "image", "trash", "arrow-up", "arrow-down", "plus", "map-pin" }
                     .Concat(DreamGrid.Launcher.Pages.Splash.IndexModel.Shelf.Select(s => s.Icon)))
            Assert.Contains(name, ids);
    }

    [Fact]
    public async Task Front_page_buttons_carry_their_icons()
    {
        await using var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        });

        using var r = await f.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        var signIn = page.QuerySelector("main form#front-sign-in button[type=submit]")!;
        Assert.Equal("/img/icons.svg#sign-in", signIn.QuerySelector("svg.btn-icon use")!.GetAttribute("href"));
        Assert.Equal("true", signIn.QuerySelector("svg")!.GetAttribute("aria-hidden"));
        Assert.Equal("Sign in", signIn.TextContent.Trim());
        Assert.Equal("/img/icons.svg#plus", page.QuerySelector("main a[href='/signup'] svg use")!.GetAttribute("href"));
        Assert.Equal("/img/icons.svg#shield", page.QuerySelector("header a#staff-link svg use")!.GetAttribute("href"));
        Assert.Equal("/img/icons.svg#help", page.QuerySelector("header a#help-link svg use")!.GetAttribute("href"));
        Assert.Null(page.Body!.GetAttribute("class")); // the narrow, centred column
    }
}
