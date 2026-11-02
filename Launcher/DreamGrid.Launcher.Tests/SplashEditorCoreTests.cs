using System.Text.Json;
using System.Text.Json.Nodes;
using DreamGrid.Launcher.Core.Splash;
using Jint;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// The splash editor's editing rules (wwwroot/js/splash-editor-core.js, plan 12c Task 3), run through Jint: snap, bounds,
/// guide lines, layers, undo, and the shrink point the preview shows, which must match the public page's.
/// </summary>
public sealed class SplashEditorCoreTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string Script = File.ReadAllText(Path.Combine(Root(), "Launcher", "DreamGrid.Launcher", "wwwroot", "js", "splash-editor-core.js"));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DreamGrid.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private readonly Engine _js = new Engine().Execute(Script);

    /// <summary>Runs <paramref name="expression"/> and returns its value as JSON.</summary>
    private JsonNode? Run(string expression) => JsonNode.Parse(_js.Evaluate("JSON.stringify(" + expression + ")").AsString());

    private static string J(object value) => JsonSerializer.Serialize(value, Json);

    private static SplashFont Font(int size) => new(size, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans);

    private static SplashBlock Block(int id, int x, int y, int w, int h, int layer = 1, int font = 24, SplashBlockKind kind = SplashBlockKind.Text) =>
        new(id, kind, x, y, w, h, layer, "#000000", 50, null, false, 8, Font(font),
            kind == SplashBlockKind.Text ? new SplashContent(Text: new Dictionary<string, string> { ["en"] = "Hi" })
                : new SplashContent(Status: new SplashStatusLines(true, true, true)));

    private static SplashDesign Page(params SplashBlock[] blocks) =>
        new(1280, 800, new SplashBackground("#1a2a3a", null, 50, 50, 0), false, 16, blocks);

    private const string Opt = "{ snap: false, snapSize: 16, others: [], tolerance: 4 }";

    private static (int X, int Y, int W, int H) Box(JsonNode? block) =>
        ((int)block!["x"]!, (int)block["y"]!, (int)block["w"]!, (int)block["h"]!);

    [Fact]
    public void Snap_rounds_to_the_grid_only_when_on()
    {
        Assert.Equal(32, (int)Run("SplashEditorCore.snap(37, 16, true)")!);
        Assert.Equal(48, (int)Run("SplashEditorCore.snap(41, 16, true)")!);
        Assert.Equal(38, (int)Run("SplashEditorCore.snap(37.6, 16, false)")!);
    }

    [Fact]
    public void Move_keeps_block_inside_the_page()
    {
        var r = Run($"SplashEditorCore.move({J(Block(1, 100, 100, 100, 100))}, -5000, 5000, {J(Page())}, {Opt})");
        Assert.Equal((0, 700, 100, 100), Box(r!["block"]));
    }

    [Fact]
    public void Move_survives_a_pointer_that_gives_no_number()
    {
        var r = Run($"SplashEditorCore.move({J(Block(1, 100, 100, 100, 100))}, NaN, undefined, {J(Page())}, {Opt})");
        Assert.Equal((100, 100, 100, 100), Box(r!["block"]));
    }

    [Fact]
    public void Move_with_snap_lands_on_the_grid()
    {
        var r = Run($"SplashEditorCore.move({J(Block(1, 0, 0, 100, 100))}, 37, 41, {J(Page())}, {{ snap: true, snapSize: 16, others: [], tolerance: 4 }})");
        Assert.Equal((32, 48, 100, 100), Box(r!["block"]));
    }

    [Fact]
    public void Move_lines_up_with_a_near_edge()
    {
        var other = J(Block(2, 300, 500, 100, 100));
        var near = Run($"SplashEditorCore.move({J(Block(1, 0, 0, 100, 100))}, 297, 0, {J(Page())}, {{ snap: false, snapSize: 16, others: [{other}], tolerance: 4 }})");
        Assert.Equal(300, (int)near!["block"]!["x"]!);
        Assert.Contains(300, near["guides"]!["x"]!.AsArray().Select(v => (int)v!));

        var far = Run($"SplashEditorCore.move({J(Block(1, 0, 0, 100, 100))}, 290, 0, {J(Page())}, {{ snap: false, snapSize: 16, others: [{other}], tolerance: 4 }})");
        Assert.Equal(290, (int)far!["block"]!["x"]!);
        Assert.DoesNotContain(300, far["guides"]!["x"]!.AsArray().Select(v => (int)v!));
    }

    [Fact]
    public void Move_lines_up_with_the_page_centre()
    {
        var r = Run($"SplashEditorCore.move({J(Block(1, 0, 0, 200, 100))}, 538, 0, {J(Page())}, {Opt})");
        Assert.Equal(540, (int)r!["block"]!["x"]!);
        Assert.Contains(640, r["guides"]!["x"]!.AsArray().Select(v => (int)v!));
    }

    [Fact]
    public void Resize_never_goes_below_the_minimum()
    {
        var r = Run($"SplashEditorCore.resize({J(Block(1, 100, 100, 200, 200))}, \"nw\", 5000, 5000, {J(Page())}, {Opt})");
        Assert.Equal((284, 284, 16, 16), Box(r!["block"]));
    }

    [Fact]
    public void Resize_never_leaves_the_page()
    {
        var r = Run($"SplashEditorCore.resize({J(Block(1, 100, 100, 200, 200))}, \"se\", 5000, 5000, {J(Page())}, {Opt})");
        Assert.Equal((100, 100, 1180, 700), Box(r!["block"]));
        var w = Run($"SplashEditorCore.resize({J(Block(1, 100, 100, 200, 200))}, \"w\", -5000, 0, {J(Page())}, {Opt})");
        Assert.Equal((0, 100, 300, 200), Box(w!["block"]));
    }

    [Fact]
    public void Nudge_moves_one_or_ten()
    {
        Assert.Equal((101, 100, 50, 50), Box(Run($"SplashEditorCore.nudge({J(Block(1, 100, 100, 50, 50))}, \"ArrowRight\", false, {J(Page())})")));
        Assert.Equal((100, 90, 50, 50), Box(Run($"SplashEditorCore.nudge({J(Block(1, 100, 100, 50, 50))}, \"ArrowUp\", true, {J(Page())})")));
    }

    [Fact]
    public void Nudge_stops_at_the_edge() =>
        Assert.Equal((0, 0, 50, 50), Box(Run($"SplashEditorCore.nudge({J(Block(1, 5, 0, 50, 50))}, \"ArrowLeft\", true, {J(Page())})")));

    [Fact]
    public void Add_block_gets_next_id_and_top_layer()
    {
        var d = Run($"SplashEditorCore.addBlock({J(Page(Block(4, 0, 0, 100, 100, layer: 7)))}, \"News\", 640, 400)");
        var added = d!["blocks"]!.AsArray()[1]!;
        Assert.Equal(5, (int)added["id"]!);
        Assert.Equal(8, (int)added["layer"]!);
        Assert.Equal("News", (string)added["kind"]!);
        Assert.Equal((440, 250, 400, 300), Box(added));
    }

    [Fact]
    public void Add_block_is_kept_inside_when_dropped_at_the_edge()
    {
        var d = Run($"SplashEditorCore.addBlock({J(Page())}, \"Text\", 1279, 799)");
        Assert.Equal((880, 680, 400, 120), Box(d!["blocks"]!.AsArray()[0]));
        Assert.Equal(1, (int)d["blocks"]!.AsArray()[0]!["id"]!);
    }

    [Fact]
    public void Duplicate_offsets_and_goes_on_top()
    {
        var d = Run($"SplashEditorCore.duplicate({J(Page(Block(1, 10, 10, 100, 100, layer: 1), Block(2, 0, 0, 50, 50, layer: 3)))}, 1)");
        var copy = d!["blocks"]!.AsArray()[2]!;
        Assert.Equal(3, (int)copy["id"]!);
        Assert.Equal(4, (int)copy["layer"]!);
        Assert.Equal((26, 26, 100, 100), Box(copy));
    }

    [Fact]
    public void Remove_takes_the_block_out()
    {
        var d = Run($"SplashEditorCore.remove({J(Page(Block(1, 0, 0, 50, 50), Block(2, 0, 0, 50, 50)))}, 1)");
        Assert.Equal([2], d!["blocks"]!.AsArray().Select(b => (int)b!["id"]!));
    }

    [Fact]
    public void Forward_and_back_swap_with_the_neighbour()
    {
        var page = J(Page(Block(1, 0, 0, 50, 50, layer: 1), Block(2, 0, 0, 50, 50, layer: 2), Block(3, 0, 0, 50, 50, layer: 5)));
        var fwd = Run($"SplashEditorCore.forward({page}, 1)")!["blocks"]!.AsArray().Select(b => (int)b!["layer"]!).ToArray();
        Assert.Equal([2, 1, 5], fwd);
        var back = Run($"SplashEditorCore.back({page}, 3)")!["blocks"]!.AsArray().Select(b => (int)b!["layer"]!).ToArray();
        Assert.Equal([1, 5, 2], back);
    }

    [Fact]
    public void Back_at_the_bottom_changes_nothing()
    {
        var page = J(Page(Block(1, 0, 0, 50, 50, layer: 1), Block(2, 0, 0, 50, 50, layer: 2)));
        Assert.Equal([1, 2], Run($"SplashEditorCore.back({page}, 1)")!["blocks"]!.AsArray().Select(b => (int)b!["layer"]!));
    }

    [Fact]
    public void Edits_never_change_their_arguments()
    {
        _js.Execute($"var d = {J(Page(Block(1, 10, 10, 100, 100)))}; var before = JSON.stringify(d);" +
            "SplashEditorCore.move(d.blocks[0], 5, 5, d, { snap: false, snapSize: 16, others: [], tolerance: 4 });" +
            "SplashEditorCore.resize(d.blocks[0], 'se', 5, 5, d, { snap: false, snapSize: 16, others: [], tolerance: 4 });" +
            "SplashEditorCore.addBlock(d, 'Text', 10, 10); SplashEditorCore.duplicate(d, 1); SplashEditorCore.forward(d, 1); SplashEditorCore.remove(d, 1);");
        Assert.True(_js.Evaluate("before === JSON.stringify(d)").AsBoolean());
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(44.6, false, 45)]
    [InlineData(190, false, -170)]
    [InlineData(-190, false, 170)]
    [InlineData(-180, false, 180)]
    [InlineData(37, true, 30)]
    [InlineData(38, true, 45)]
    [InlineData(double.NaN, false, 0)]
    public void Angle_is_whole_degrees_within_a_half_turn(double raw, bool shift, int expected) =>
        Assert.Equal(expected, (int)Run($"SplashEditorCore.angle({(double.IsNaN(raw) ? "NaN" : raw.ToString(System.Globalization.CultureInfo.InvariantCulture))}, {(shift ? "true" : "false")})")!);

    [Theory]
    [InlineData(100, 100, 100, 0, 0)]      // straight above the centre: upright
    [InlineData(100, 100, 200, 100, 90)]   // to the right: a quarter turn clockwise
    [InlineData(100, 100, 100, 200, 180)]  // below: half a turn
    [InlineData(100, 100, 0, 100, -90)]    // to the left: a quarter turn back
    public void Pointer_angle_is_measured_from_straight_up(double cx, double cy, double px, double py, int expected) =>
        Assert.Equal(expected, (int)Run($"SplashEditorCore.pointerAngle({cx}, {cy}, {px}, {py}, false)")!);

    [Fact]
    public void New_blocks_are_upright() =>
        Assert.Equal(0, (int)Run($"SplashEditorCore.addBlock({J(Page())}, \"Text\", 640, 400).blocks[0].rotate")!);

    [Fact]
    public void Set_path_copies_and_changes_one_field()
    {
        var news = new SplashBlock(1, SplashBlockKind.News, 0, 0, 100, 100, 1, "#000000", 50, null, false, 8, Font(24),
            new SplashContent(Heading: new Dictionary<string, string> { ["en"] = "News" },
                News: [new SplashNewsItem(new DateOnly(2026, 11, 1), new Dictionary<string, string> { ["en"] = "Hi" }, false)]));
        _js.Execute($"var b = {J(news)}; var before = JSON.stringify(b);" +
            "var big = SplashEditorCore.setPath(b, 'font.size', 40); var hidden = SplashEditorCore.setPath(b, 'content.news.0.hidden', true);");
        Assert.True(_js.Evaluate("before === JSON.stringify(b)").AsBoolean());
        Assert.Equal(40, (int)Run("big.font.size")!);
        Assert.Equal(24, (int)Run("hidden.font.size")!);
        Assert.True((bool)Run("hidden.content.news[0].hidden")!);
        Assert.Equal("Hi", (string)Run("hidden.content.news[0].text.en")!);
    }

    [Fact]
    public void Fit_blocks_keeps_every_block_inside_a_smaller_page()
    {
        var d = Run($"SplashEditorCore.fitBlocks(SplashEditorCore.setPath({J(Page(Block(1, 1000, 600, 200, 150)))}, 'width', 800))");
        Assert.Equal((600, 600, 200, 150), Box(d!["blocks"]!.AsArray()[0]));
    }

    [Fact]
    public void History_undo_redo()
    {
        _js.Execute("var h = SplashEditorCore.History(100); h.push({ n: 'A' }); h.push({ n: 'B' }); h.push({ n: 'C' });");
        Assert.Equal("B", (string)Run("h.undo().n")!);
        Assert.Equal("A", (string)Run("h.undo().n")!);
        Assert.Null(Run("h.undo()"));
        Assert.Equal("B", (string)Run("h.redo().n")!);
        Assert.True((bool)Run("h.canRedo()")!);
    }

    [Fact]
    public void New_edit_clears_redo()
    {
        _js.Execute("var h = SplashEditorCore.History(100); h.push({ n: 'A' }); h.push({ n: 'B' }); h.push({ n: 'C' }); h.undo(); h.push({ n: 'D' });");
        Assert.Null(Run("h.redo()"));
        Assert.False((bool)Run("h.canRedo()")!);
        Assert.Equal("B", (string)Run("h.undo().n")!);
    }

    [Fact]
    public void History_drops_the_oldest_beyond_the_limit()
    {
        _js.Execute("var h = SplashEditorCore.History(100); for (var i = 0; i < 105; i++) h.push({ n: i }); var undos = 0; while (h.undo()) undos++;");
        Assert.Equal(99, (int)Run("undos")!);
    }

    public static TheoryData<string> Designs => ["none", "12", "24", "200"];

    private static SplashDesign DesignFor(string fonts) => fonts == "none"
        ? Page(Block(1, 0, 0, 100, 100, kind: SplashBlockKind.Picture) with { Content = new SplashContent(Picture: new SplashPicture("a.png", SplashFit.Fill)) })
        : Page(Block(1, 0, 0, 100, 100, font: int.Parse(fonts)), Block(2, 0, 0, 100, 100, font: 48));

    [Theory]
    [MemberData(nameof(Designs))]
    public void Core_min_width_matches_server(string fonts)
    {
        var d = DesignFor(fonts);
        Assert.Equal(SplashCss.MinWidth(d), (int)Run($"SplashEditorCore.minWidth({J(d)})")!);
        Assert.Equal(SplashCss.MaxWidth(d), (int)Run($"SplashEditorCore.maxWidth({J(d)})")!);
        Assert.Equal(Math.Clamp(1024, SplashCss.MinWidth(d), SplashCss.MaxWidth(d)), (int)Run($"SplashEditorCore.previewWidth({J(d)}, 1024)")!);
        Assert.Equal(Math.Clamp(1920, SplashCss.MinWidth(d), SplashCss.MaxWidth(d)), (int)Run($"SplashEditorCore.previewWidth({J(d)}, 1920)")!);
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("GridName")]
    [InlineData("Status")]
    [InlineData("News")]
    [InlineData("Places")]
    [InlineData("Links")]
    public void Every_default_block_passes_the_rules(string kind)
    {
        var json = _js.Evaluate($"JSON.stringify(SplashEditorCore.addBlock({J(SplashDefaults.Design())}, \"{kind}\", 640, 400))").AsString();
        var d = JsonSerializer.Deserialize<SplashDesign>(json, Json);
        Assert.Empty(SplashRules.Check(d, _ => true));
    }
}
