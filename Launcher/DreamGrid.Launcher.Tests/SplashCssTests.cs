using System.Globalization;
using DreamGrid.Launcher.Core.Splash;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The stylesheet each splash design gets: proportions, shrink-then-scroll, colours and picture fit (spec §6).</summary>
public sealed class SplashCssTests
{
    private static SplashFont Font(int size) => new(size, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans);

    private static SplashBlock Text(int id, int x, int y, int w, int h, int size) =>
        new(id, SplashBlockKind.Text, x, y, w, h, id, "#ff0000", 25, null, false, 8, Font(size),
            new SplashContent(Text: new Dictionary<string, string> { ["en"] = "Hi" }));

    private static SplashDesign Page(int width, params SplashBlock[] blocks) =>
        new(width, 800, new SplashBackground("#1a2a3a", null, 30, 70, 40), false, 16, blocks);

    /// <summary>A text block per font size, so the smallest one decides the minimum width.</summary>
    private static SplashDesign WithFonts(int width, params int[] sizes) =>
        Page(width, sizes.Select((s, i) => Text(i + 1, 0, 0, 100, 100, s)).ToArray());

    private static SplashDesign OneBlock(int x, int y, int w, int h) => Page(1280, Text(1, x, y, w, h, 32));

    private static string Css(SplashDesign d) => SplashCss.Build(d, n => "/x/" + n).Replace(" ", "");

    [Fact]
    public void Min_width_is_where_smallest_text_is_12px() => Assert.Equal(960, SplashCss.MinWidth(WithFonts(1280, 16, 40)));

    [Fact]
    public void Min_width_never_below_half() => Assert.Equal(640, SplashCss.MinWidth(WithFonts(1280, 48)));

    [Fact]
    public void Min_width_never_above_design() => Assert.Equal(1280, SplashCss.MinWidth(WithFonts(1280, 10)));

    [Fact]
    public void Min_width_of_a_page_without_text_is_half() => Assert.Equal(640, SplashCss.MinWidth(Page(1280)));

    [Fact]
    public void Max_width_is_one_and_a_half() => Assert.Equal(1920, SplashCss.MaxWidth(WithFonts(1280, 16)));

    [Fact]
    public void Rotated_block_is_turned_about_its_centre() =>
        Assert.Contains("transform:rotate(-30deg)", Css(Page(1280, Text(1, 0, 0, 100, 100, 32) with { Rotate = -30 })));

    [Fact]
    public void Upright_block_has_no_transform() =>
        Assert.DoesNotContain("transform", Css(OneBlock(0, 0, 100, 100)));

    [Fact]
    public void Page_selector_is_used_for_the_background() =>
        Assert.StartsWith("body.splash, .splash-canvas{", SplashCss.Build(OneBlock(0, 0, 100, 100), n => "/x/" + n, "body.splash, .splash-canvas"));

    [Fact]
    public void Default_selector_is_body_splash() =>
        Assert.StartsWith("body.splash{", SplashCss.Build(OneBlock(0, 0, 100, 100), n => "/x/" + n));

    [Fact]
    public void Block_position_is_a_percentage() =>
        Assert.Contains(".b-1{left:25%;top:25%;width:50%;height:50%", Css(OneBlock(320, 200, 640, 400)));

    [Fact]
    public void Font_size_has_px_then_cqw()
    {
        var css = Css(OneBlock(0, 0, 100, 100));
        var px = css.IndexOf("font-size:32px", StringComparison.Ordinal);
        var cqw = css.IndexOf("font-size:2.5cqw", StringComparison.Ordinal);
        Assert.True(px >= 0 && cqw > px, css);
    }

    [Fact]
    public void See_through_becomes_alpha() => Assert.Contains("rgba(255,0,0,0.75)", Css(OneBlock(0, 0, 100, 100)));

    [Theory]
    [InlineData(SplashFit.Fill, "background-size:cover")]
    [InlineData(SplashFit.Fit, "background-size:contain")]
    [InlineData(SplashFit.Stretch, "background-size:100%100%")]
    [InlineData(SplashFit.Tile, "background-repeat:repeat")]
    [InlineData(SplashFit.Centre, "background-position:center")]
    public void Background_fit_maps_to_css(SplashFit fit, string expected)
    {
        var name = new string('a', 32) + ".png";
        var d = Page(1280) with { Background = new SplashBackground("#1a2a3a", new SplashPicture(name, fit), 30, 70, 40) };
        var css = Css(d);
        Assert.Contains(expected, css);
        Assert.Contains("url(\"/x/" + name + "\")", css);
        if (fit != SplashFit.Centre) Assert.Contains("background-position:30%70%", css);
        Assert.Contains("rgba(0,0,0,0.4)", css); // the darkening layer
    }

    [Fact]
    public void Numbers_use_invariant_culture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var css = Css(OneBlock(1, 1, 333, 333));
            Assert.DoesNotContain(",", css.Replace("rgba(", "").Split("rgba")[0].Split("{")[1]);
            Assert.Contains("0.0781%", css); // 1 / 1280
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Page_box_has_clamp_and_container()
    {
        var css = Css(WithFonts(1280, 16));
        Assert.Contains("width:clamp(960px,100vw,1920px)", css);
        Assert.Contains("aspect-ratio:1280/800", css);
        Assert.Contains("container-type:inline-size", css);
    }

    [Fact]
    public void Page_background_also_fills_the_body() => Assert.Contains("body.splash{background-color:#1a2a3a", Css(Page(1280)));

    [Fact]
    public void Rounded_only_when_asked()
    {
        Assert.DoesNotContain("border-radius", Css(OneBlock(0, 0, 100, 100)));
        Assert.Contains("border-radius", Css(Page(1280, Text(1, 0, 0, 100, 100, 32) with { Rounded = true })));
    }

    [Fact]
    public void Class_of_a_block() => Assert.Equal("b-17", SplashCss.ClassOf(Text(17, 0, 0, 100, 100, 32)));

    private static readonly string PictureName = new string('a', 32) + ".png";

    [Fact]
    public void Picture_block_gets_its_own_rule()
    {
        var picture = new SplashBlock(1, SplashBlockKind.Picture, 0, 0, 400, 300, 1, "#000000", 100, null, false, 0, Font(24),
            new SplashContent(Picture: new SplashPicture(PictureName, SplashFit.Fit)));

        var css = Css(Page(1280, picture));

        Assert.Equal("b-1-pic", SplashCss.PictureClassOf(picture));
        Assert.Contains(".b-1-pic{background-image:url(\"/x/" + PictureName + "\");background-size:contain", css);
    }

    [Fact]
    public void Place_pictures_get_rules()
    {
        var places = new SplashBlock(1, SplashBlockKind.Places, 0, 0, 400, 300, 1, "#000000", 100, null, false, 0, Font(24),
            new SplashContent(Places:
            [
                new SplashPlace(Guid.NewGuid(), "Beach", new Dictionary<string, string> { ["en"] = "" }, null),
                new SplashPlace(Guid.NewGuid(), "Sandbox", new Dictionary<string, string> { ["en"] = "" }, PictureName),
            ]));

        var css = Css(Page(1280, places));

        Assert.Equal("b-1-place-1", SplashCss.PlaceClassOf(places, 1));
        Assert.DoesNotContain(".b-1-place-0{", css);
        Assert.Contains(".b-1-place-1{background-image:url(\"/x/" + PictureName + "\");background-size:cover", css);
    }
}
