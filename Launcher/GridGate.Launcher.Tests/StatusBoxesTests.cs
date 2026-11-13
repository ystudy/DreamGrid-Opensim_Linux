using System.Text.Encodings.Web;
using GridGate.Launcher.Navigation;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Xunit;

namespace GridGate.Launcher.Tests;

public class StatusBoxesTests
{
    private static string Render(TagHelper helper, string tag)
    {
        var context = new TagHelperContext(tag, [], new Dictionary<object, object>(), "t");
        var output = new TagHelperOutput(tag, [], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()))
        {
            TagMode = TagMode.SelfClosing,
        };
        helper.Process(context, output);
        using var writer = new StringWriter();
        output.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    [Fact]
    public void Error_box_is_the_danger_div() =>
        Assert.Equal("<div class=\"error-box error alert alert-danger\" role=\"alert\">No avatar called Ann &lt;b&gt; &amp; co.</div>",
            Render(new ErrorBoxTagHelper { Text = "No avatar called Ann <b> & co." }, "error-box"));

    [Fact]
    public void Ok_box_is_the_success_div() =>
        Assert.Equal("<div class=\"ok-box alert alert-success\" role=\"status\">Saved.</div>",
            Render(new OkBoxTagHelper { Text = "Saved." }, "ok-box"));

    [Fact]
    public void Notice_box_is_the_info_paragraph() =>
        Assert.Equal("<p class=\"notice alert alert-info py-2\" role=\"status\">Moving Home.</p>",
            Render(new NoticeBoxTagHelper { Text = "Moving Home." }, "notice-box"));

    [Fact]
    public void Null_text_writes_nothing() =>
        Assert.Equal("", Render(new ErrorBoxTagHelper { Text = null }, "error-box"));

    [Fact]
    public void Empty_text_still_writes_the_box() =>
        Assert.Equal("<div class=\"ok-box alert alert-success\" role=\"status\"></div>",
            Render(new OkBoxTagHelper { Text = "" }, "ok-box"));
}
