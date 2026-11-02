using DreamGrid.Launcher.Core.Splash;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>What a splash design may hold before it is saved (spec 2026-11-01-viewer-splash-design §5). All content is made up.</summary>
public sealed class SplashRulesTests
{
    private static readonly SplashFont Font = new(24, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans);

    private static Dictionary<string, string> En(string text) => new() { ["en"] = text };

    private static SplashBlock Block(int id, SplashBlockKind kind, SplashContent content) =>
        new(id, kind, 40, 40 + id * 10, 400, 80, id, "#000000", 50, null, false, 8, Font, content);

    /// <summary>A 1280 × 800 design with one block of each kind, every field inside its limits, and no pictures.</summary>
    private static SplashDesign Valid() => new(1280, 800, new SplashBackground("#1a2a3a", null, 50, 50, 20), false, 16,
    [
        Block(1, SplashBlockKind.Text, new(Text: En("Welcome to the grid."))),
        Block(2, SplashBlockKind.GridName, new(ShowName: true)),
        Block(3, SplashBlockKind.Status, new(Status: new SplashStatusLines(true, true, true))),
        Block(4, SplashBlockKind.News, new(Heading: En("News"), News: [new SplashNewsItem(new DateOnly(2026, 11, 1), En("Party at Sandbox."), false)])),
        Block(5, SplashBlockKind.Places, new(Places: [new SplashPlace(Guid.NewGuid(), "Sandbox", En("Build here."), null)])),
        Block(6, SplashBlockKind.Links, new(Links: new SplashLinks(true, true, true, true, false))),
    ]);

    private static SplashDesign WithBlock(SplashBlock b) => Valid() with { Blocks = [b] };

    [Fact]
    public void Valid_design_passes() => Assert.Empty(SplashRules.Check(Valid(), _ => false));

    [Theory]
    [InlineData(639, 800)]
    [InlineData(3841, 800)]
    [InlineData(1280, 399)]
    [InlineData(1280, 2161)]
    public void Design_size_out_of_bounds_is_refused(int w, int h) =>
        Assert.NotEmpty(SplashRules.Check(Valid() with { Width = w, Height = h, Blocks = [] }, _ => false));

    [Fact]
    public void Check_uses_current_design_size()
    {
        // A block at x=1500 is outside a 1280-wide page but inside a 1920-wide one.
        var d = WithBlock(Valid().Blocks[0] with { X = 1500, W = 100 });
        Assert.NotEmpty(SplashRules.Check(d, _ => false));
        Assert.Empty(SplashRules.Check(d with { Width = 1920 }, _ => false));
    }

    [Fact]
    public void Block_outside_page_is_refused()
    {
        var b = Valid().Blocks[0];
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { X = 1000, W = 281 }), _ => false));
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { Y = 700, H = 101 }), _ => false));
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { X = -1 }), _ => false));
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { W = 15 }), _ => false));
        Assert.Empty(SplashRules.Check(WithBlock(b with { X = 0, Y = 0, W = 1280, H = 800 }), _ => false));
    }

    [Fact]
    public void More_than_100_blocks_is_refused()
    {
        var b = Valid().Blocks[0];
        var many = Enumerable.Range(1, 101).Select(i => b with { Id = i, Y = 0 }).ToList();
        Assert.NotEmpty(SplashRules.Check(Valid() with { Blocks = many }, _ => false));
        Assert.Empty(SplashRules.Check(Valid() with { Blocks = many.Take(100).ToList() }, _ => false));
    }

    [Fact]
    public void Duplicate_block_ids_are_refused()
    {
        var b = Valid().Blocks[0];
        Assert.NotEmpty(SplashRules.Check(Valid() with { Blocks = [b, b] }, _ => false));
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { Id = 0 }), _ => false));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("#12345g")]
    [InlineData("#1234567")]
    public void Bad_colour_is_refused(string colour)
    {
        var b = Valid().Blocks[0];
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { Colour = colour }), _ => false));
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { Font = Font with { Colour = colour } }), _ => false));
        Assert.NotEmpty(SplashRules.Check(Valid() with { Background = Valid().Background with { Colour = colour } }, _ => false));
    }

    [Fact]
    public void Numbers_out_of_range_are_refused()
    {
        var b = Valid().Blocks[0];
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { SeeThrough = 101 }), _ => false));
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { Padding = 65 }), _ => false));
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { Layer = 1000 }), _ => false));
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { Font = Font with { Size = 7 } }), _ => false));
        Assert.NotEmpty(SplashRules.Check(WithBlock(b with { Font = Font with { Size = 201 } }), _ => false));
        Assert.NotEmpty(SplashRules.Check(Valid() with { Background = Valid().Background with { Darken = 81 } }, _ => false));
        Assert.NotEmpty(SplashRules.Check(Valid() with { Background = Valid().Background with { FocusX = 101 } }, _ => false));
        Assert.NotEmpty(SplashRules.Check(Valid() with { SnapSize = 10 }, _ => false));
    }

    [Fact]
    public void Field_of_another_kind_is_refused()
    {
        var status = Valid().Blocks[2];
        Assert.NotEmpty(SplashRules.Check(WithBlock(status with { Content = status.Content with { News = [] } }), _ => false));
    }

    [Fact]
    public void Missing_required_field_is_refused()
    {
        var text = Valid().Blocks[0];
        Assert.NotEmpty(SplashRules.Check(WithBlock(text with { Content = new SplashContent() }), _ => false));
    }

    [Fact]
    public void Language_other_than_en_is_refused()
    {
        var text = Valid().Blocks[0];
        Assert.NotEmpty(SplashRules.Check(WithBlock(text with { Content = new(Text: new Dictionary<string, string> { ["de"] = "Hallo" }) }), _ => false));
    }

    [Fact]
    public void Overlong_text_is_refused()
    {
        var text = Valid().Blocks[0];
        Assert.NotEmpty(SplashRules.Check(WithBlock(text with { Content = new(Text: En(new string('x', 4001))) }), _ => false));
        Assert.Empty(SplashRules.Check(WithBlock(text with { Content = new(Text: En(new string('x', 4000))) }), _ => false));
    }

    [Fact]
    public void Too_many_news_items_or_places_is_refused()
    {
        var news = Valid().Blocks[3];
        var item = news.Content.News![0];
        Assert.NotEmpty(SplashRules.Check(WithBlock(news with { Content = news.Content with { News = Enumerable.Repeat(item, 21).ToList() } }), _ => false));
        var places = Valid().Blocks[4];
        var place = places.Content.Places![0];
        Assert.NotEmpty(SplashRules.Check(WithBlock(places with { Content = places.Content with { Places = Enumerable.Repeat(place, 9).ToList() } }), _ => false));
    }

    [Fact]
    public void Picture_must_exist_and_be_well_named()
    {
        var name = new string('a', 32) + ".png";
        var d = Valid() with { Background = Valid().Background with { Picture = new SplashPicture(name, SplashFit.Fill) } };
        Assert.NotEmpty(SplashRules.Check(d, _ => false));
        Assert.Empty(SplashRules.Check(d, n => n == name));
        var bad = d with { Background = d.Background with { Picture = new SplashPicture("../settings.json", SplashFit.Fill) } };
        Assert.NotEmpty(SplashRules.Check(bad, _ => true));
    }

    [Fact]
    public void Problems_name_the_block()
    {
        var problems = SplashRules.Check(WithBlock(Valid().Blocks[0] with { Id = 7, Colour = "red" }), _ => false);
        Assert.Contains(problems, p => p.Contains("7"));
    }
}
